use bytes::Bytes;
use prost::Message;
use sha2::{Digest, Sha256};
use thiserror::Error;

use tb_core::{
    AdmissionError as ProcessAdmissionError, Attribution, ItemReservation, MemoryBudget,
    Reservation, TransactionId, WorkReservation,
};
use tb_format::decode_wal_frame;
use tb_format::{
    EnvelopeCodec, FormatError, FormatLimits, decode_envelope, encode_envelope_with_codec_into,
    encode_lz4_envelope_into, inspect_envelope,
};

use crate::{
    AdmissionController, AdmissionError, BatchPermit, CreditError, MAX_DECODED_APPLICATION_BYTES,
    MAX_ENCODED_APPLICATION_BYTES, MAX_EVENT_METADATA_BYTES, MAX_GRPC_MESSAGE_BYTES,
    MAX_TRANSACTION_GROUP_BYTES, PeerCreditPermit, PeerCreditWindow, V1_EVENT_FORMAT,
    V1_PROTOCOL_MAJOR,
    handshake::{validate_negotiated_codecs, validate_transfer_limits},
    v1::{self, PayloadCodec},
};

/// Metadata supplied by the committed-event/storage boundary for one whole group.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommittedGroupMetadata {
    /// Committed origin identity and single-event sequence.
    pub event_id: v1::CommittedEventId,
    /// Certified membership epoch.
    pub membership_epoch: u64,
    /// Certified membership configuration SHA-256 identity.
    pub configuration_id: Bytes,
    /// Canonical 24-byte transaction retry identity.
    pub transaction_id: Bytes,
    /// Committed event format version.
    pub event_format_version: u32,
    /// Binary record format version.
    pub record_format_version: u32,
    /// Merge policy version for all document changes in the group.
    pub merge_policy_version: u32,
    /// Complete encoded distributed event metadata, supplied by storage.
    pub distributed_metadata: Bytes,
    /// Logical document changes, supplied by the event/storage boundary.
    pub change_count: usize,
}

/// An encoded group retaining its transient byte/batch admission reservation.
#[derive(Debug)]
pub struct EncodedCommittedGroup {
    batch: v1::CommittedGroupBatch,
    permit: BatchPermit,
}

/// A bounded transport payload whose bytes and admission permits remain owned.
///
/// This proves only that the transport envelope, WAL frame, transaction ID,
/// and event digest passed wire-level checks. Event identity, epoch, and
/// configuration fields remain sender claims until a semantic decoder and
/// certified authority validate them. Dropping this value releases both the
/// per-peer and process reservations.
pub struct DecodedTransportGroup {
    batch: v1::CommittedGroupBatch,
    wal_frame: Bytes,
    digest: [u8; 32],
    transaction_id: TransactionId,
    codec: PayloadCodec,
    process_bytes: usize,
    _peer_permit: BatchPermit,
    _predecode_work: Option<WorkReservation>,
    _process_memory: Reservation,
    _process_item: ItemReservation,
}

/// A committed-group Protobuf request admitted before Prost materializes it.
///
/// The exact decoded request and both admission guards are inseparable. Its
/// fields and constructor are private so callers cannot pair a cheaper
/// preflight reservation with a different batch.
pub struct PreAdmittedBatch {
    batch: v1::CommittedGroupBatch,
    peer_permit: BatchPermit,
    predecode_work: WorkReservation,
}

impl PreAdmittedBatch {
    pub(crate) fn new(
        batch: v1::CommittedGroupBatch,
        peer_permit: BatchPermit,
        predecode_work: WorkReservation,
    ) -> Self {
        Self {
            batch,
            peer_permit,
            predecode_work,
        }
    }

    /// The already-decoded batch protected by these admission guards.
    #[must_use]
    pub const fn batch(&self) -> &v1::CommittedGroupBatch {
        &self.batch
    }
}

impl DecodedTransportGroup {
    /// Exact distributed metadata bytes from the received Protobuf message.
    #[must_use]
    pub fn metadata_bytes(&self) -> &[u8] {
        self.batch.distributed_metadata.as_ref()
    }

    /// Exact complete CRC-checked WAL frame bytes.
    #[must_use]
    pub fn wal_frame_bytes(&self) -> &[u8] {
        self.wal_frame.as_ref()
    }

    /// SHA-256 verified over the exact metadata and complete WAL bytes.
    #[must_use]
    pub const fn event_digest(&self) -> [u8; 32] {
        self.digest
    }

    /// Untrusted event identity claim carried by the transport header.
    #[must_use]
    pub fn event_id_claim(&self) -> Option<&v1::CommittedEventId> {
        self.batch.event_id.as_ref()
    }

    /// Transaction ID checked against the complete WAL frame.
    #[must_use]
    pub const fn transaction_id(&self) -> TransactionId {
        self.transaction_id
    }

    /// Untrusted membership epoch claim carried by the transport header.
    #[must_use]
    pub const fn membership_epoch_claim(&self) -> u64 {
        self.batch.membership_epoch
    }

    /// Untrusted configuration identity claim carried by the transport header.
    #[must_use]
    pub fn configuration_id_claim(&self) -> &[u8] {
        self.batch.configuration_id.as_ref()
    }

    /// Untrusted logical change count claim carried by the transport header.
    #[must_use]
    pub const fn change_count_claim(&self) -> u32 {
        self.batch.change_count
    }

    /// Untrusted format declarations carried by the transport header.
    #[must_use]
    pub const fn format_version_claims(&self) -> (u32, u32, u32) {
        (
            self.batch.event_format_version,
            self.batch.record_format_version,
            self.batch.merge_policy_version,
        )
    }

    /// Validated codec selected for the envelope.
    #[must_use]
    pub const fn codec(&self) -> PayloadCodec {
        self.codec
    }

    /// Declared encoded application payload bytes, checked against actual fields.
    #[must_use]
    pub const fn encoded_application_bytes(&self) -> u32 {
        self.batch.encoded_application_bytes
    }

    /// Declared decoded application payload bytes, checked against actual lengths.
    #[must_use]
    pub const fn decoded_application_bytes(&self) -> u32 {
        self.batch.decoded_application_bytes
    }

    /// Conservative application credit cost for this complete transport group.
    #[must_use]
    pub fn credit_bytes(&self) -> usize {
        self.batch.encoded_application_bytes as usize
            + self.batch.decoded_application_bytes as usize
    }

    /// Process-budget charge held until this value is dropped. It covers the
    /// logical input message size, compact retained Protobuf byte copies,
    /// decoded WAL bytes, and the fixed owned container size.
    #[must_use]
    pub const fn process_reservation_bytes(&self) -> usize {
        self.process_bytes
    }
}

impl EncodedCommittedGroup {
    /// Protobuf message to put on the already-negotiated replication stream.
    #[must_use]
    pub const fn batch(&self) -> &v1::CommittedGroupBatch {
        &self.batch
    }

    /// Exact conservative application credit cost: combined encoded and
    /// decoded bytes, each of which includes distributed metadata.
    #[must_use]
    pub fn credit_bytes(&self) -> usize {
        self.batch.encoded_application_bytes as usize
            + self.batch.decoded_application_bytes as usize
    }

    /// Reserves the peer's advertised credit before this message is queued.
    ///
    /// # Errors
    ///
    /// Returns [`CreditError`] when the peer's current byte grant is closed or exhausted.
    pub fn reserve_peer_credit<'a>(
        &self,
        window: &'a PeerCreditWindow,
    ) -> Result<PeerCreditPermit<'a>, CreditError> {
        window.reserve(self.credit_bytes())
    }

    /// Splits the wire message from the reservation that must live until send/cancel.
    #[must_use]
    pub fn into_parts(self) -> (v1::CommittedGroupBatch, BatchPermit) {
        (self.batch, self.permit)
    }
}

/// Batch, envelope, or event-digest validation failure.
#[derive(Debug, Error)]
pub enum ProtocolError {
    /// A required v1 field or invariant is invalid.
    #[error("invalid replication protocol field: {0}")]
    Invalid(&'static str),
    /// A declared or observed size exceeds a v1 transfer limit.
    #[error("replication transfer limit exceeded: {0}")]
    LimitExceeded(&'static str),
    /// The declared codec does not match the checked application envelope.
    #[error("declared codec does not match the application envelope")]
    CodecMismatch,
    /// The application codec was not part of the negotiated allow-set.
    #[error("application codec was not negotiated")]
    CodecNotNegotiated,
    /// The canonical event SHA-256 differs from the transmitted digest.
    #[error("committed event SHA-256 digest mismatch")]
    EventDigestMismatch,
    /// The format decoder rejected a malformed or corrupt envelope.
    #[error(transparent)]
    Format(#[from] FormatError),
    /// The complete WAL transaction identity differs from the distributed header.
    #[error("WAL frame transaction identity differs from the committed group")]
    TransactionMismatch,
    /// No canonical distributed metadata decoder and WAL/document mapper is registered.
    #[error("unsupported distributed event metadata encoding")]
    UnsupportedMetadataEncoding,
    /// Bounded per-peer admission rejected work before expansion.
    #[error(transparent)]
    Admission(#[from] AdmissionError),
    /// Process-wide admission rejected transport bytes or work items.
    #[error("process replication memory admission failed: {0}")]
    ProcessAdmission(#[from] ProcessAdmissionError),
    /// The latest cgroup/RSS sample rejected the next pre-decode reservation.
    #[error("process memory headroom gate rejected incoming replication frame")]
    ProcessHeadroomRejected,
}

/// Encodes one complete WAL frame and supplied metadata using the negotiated
/// identity/LZ4 v1 codec allow-set.
///
/// The returned object owns a permit for the v1 maximum combined encoded
/// application payload. The canonical event digest is computed from the exact
/// metadata and complete WAL bytes. Retain the permit while the message is
/// queued or being sent; drop or cancel it to release the reservation. This
/// helper does not publish a received/durable/applied frontier or issue any
/// acknowledgment. Pass the validated codec allow-set from the negotiated
/// Welcome.
///
/// # Errors
///
/// Returns [`ProtocolError::Invalid`] for malformed identities, versions,
/// digests, or codec sets; [`ProtocolError::LimitExceeded`] for hard or
/// negotiated size/count caps; [`ProtocolError::Format`] when the envelope or
/// WAL frame cannot be encoded; and [`ProtocolError::Admission`] when the
/// per-peer budget is exhausted.
pub fn encode_committed_group(
    metadata: CommittedGroupMetadata,
    wal_frame_bytes: &[u8],
    output: &mut Vec<u8>,
    admission: &AdmissionController,
    negotiated_codecs: &[PayloadCodec],
    negotiated_limits: &v1::TransferLimits,
) -> Result<EncodedCommittedGroup, ProtocolError> {
    validate_negotiated_codecs(negotiated_codecs)?;
    validate_transfer_limits(negotiated_limits)?;
    validate_metadata(&metadata)?;
    validate_application_len(wal_frame_bytes.len())?;
    let metadata_bytes = metadata.distributed_metadata.len();
    let decoded_application_bytes = metadata_bytes
        .checked_add(wal_frame_bytes.len())
        .ok_or(ProtocolError::LimitExceeded("decoded application bytes"))?;
    if wal_frame_bytes.len() > negotiated_limits.max_transaction_group_bytes as usize
        || metadata_bytes > negotiated_limits.max_event_metadata_bytes as usize
        || decoded_application_bytes > negotiated_limits.max_decoded_application_bytes as usize
    {
        return Err(ProtocolError::LimitExceeded("negotiated group limits"));
    }

    let wal = decode_wal_frame(wal_frame_bytes, &format_limits(negotiated_limits))?;
    let transaction_id = TransactionId::from_bytes(
        metadata
            .transaction_id
            .as_ref()
            .try_into()
            .map_err(|_| ProtocolError::Invalid("transaction identity"))?,
    );
    if wal.transaction_id() != transaction_id {
        return Err(ProtocolError::TransactionMismatch);
    }
    let reservation = negotiated_limits.max_encoded_application_bytes as usize;
    let permit = admission.try_admit_batch(reservation)?;
    let mut limits = FormatLimits::local_v1();
    limits.max_frame_bytes = negotiated_limits.max_transaction_group_bytes as usize;
    limits.max_operations = crate::MAX_WAL_OPERATIONS_PER_GROUP;
    limits.max_envelope_encoded_bytes =
        (negotiated_limits.max_encoded_application_bytes as usize).saturating_sub(metadata_bytes);
    limits.max_envelope_plain_bytes = negotiated_limits.max_transaction_group_bytes as usize;
    let allows_identity = negotiated_codecs.contains(&PayloadCodec::Identity);
    let allows_lz4 = negotiated_codecs.contains(&PayloadCodec::Lz4BlockV1);
    let codec = if allows_identity {
        if allows_lz4 {
            encode_lz4_envelope_into(output, wal_frame_bytes, &limits)?
        } else {
            encode_envelope_with_codec_into(
                output,
                wal_frame_bytes,
                EnvelopeCodec::Identity,
                &limits,
            )?
        }
    } else {
        encode_envelope_with_codec_into(output, wal_frame_bytes, EnvelopeCodec::Lz4, &limits)?
    };
    let encoded_envelope_len = output.len();
    let encoded_application_bytes = encoded_envelope_len
        .checked_add(metadata_bytes)
        .ok_or(ProtocolError::LimitExceeded("encoded application bytes"))?;
    if encoded_application_bytes > negotiated_limits.max_encoded_application_bytes as usize {
        return Err(ProtocolError::LimitExceeded("encoded application bytes"));
    }
    let event_digest = event_digest_sha256(&metadata.distributed_metadata, wal_frame_bytes)?;

    let batch = v1::CommittedGroupBatch {
        protocol_major: V1_PROTOCOL_MAJOR,
        event_format_version: metadata.event_format_version,
        record_format_version: metadata.record_format_version,
        merge_policy_version: metadata.merge_policy_version,
        membership_epoch: metadata.membership_epoch,
        configuration_id: metadata.configuration_id,
        event_id: Some(metadata.event_id),
        transaction_id: metadata.transaction_id,
        event_digest_sha256: Bytes::copy_from_slice(event_digest.as_ref()),
        codec: codec_to_wire(codec) as i32,
        wal_group_bytes: u32::try_from(wal_frame_bytes.len())
            .map_err(|_| ProtocolError::LimitExceeded("WAL group bytes"))?,
        event_metadata_bytes: u32::try_from(metadata_bytes)
            .map_err(|_| ProtocolError::LimitExceeded("event metadata bytes"))?,
        change_count: u32::try_from(metadata.change_count)
            .map_err(|_| ProtocolError::LimitExceeded("change count"))?,
        encoded_application_bytes: u32::try_from(encoded_application_bytes)
            .map_err(|_| ProtocolError::LimitExceeded("encoded application bytes"))?,
        decoded_application_bytes: u32::try_from(decoded_application_bytes)
            .map_err(|_| ProtocolError::LimitExceeded("decoded application bytes"))?,
        application_envelope: Bytes::from(std::mem::take(output)),
        distributed_metadata: metadata.distributed_metadata,
    };
    if batch.encoded_len() > negotiated_limits.max_grpc_message_bytes as usize {
        return Err(ProtocolError::LimitExceeded("gRPC message bytes"));
    }
    Ok(EncodedCommittedGroup { batch, permit })
}

/// Checks scalar and identity declarations without decompressing or allocating.
///
/// # Errors
///
/// Returns [`ProtocolError::Invalid`] for malformed fields or inconsistent
/// declarations and [`ProtocolError::LimitExceeded`] for a hard v1 cap violation.
pub fn validate_committed_group_header(
    batch: &v1::CommittedGroupBatch,
) -> Result<(), ProtocolError> {
    validate_group_header(batch, None)
}

/// Checks a header against the hard v1 and negotiated stream limits.
///
/// # Errors
///
/// Returns [`ProtocolError::Invalid`] for malformed fields or inconsistent
/// declarations and [`ProtocolError::LimitExceeded`] for hard or negotiated caps.
pub fn validate_committed_group_header_with_limits(
    batch: &v1::CommittedGroupBatch,
    negotiated_limits: &v1::TransferLimits,
) -> Result<(), ProtocolError> {
    validate_transfer_limits(negotiated_limits)?;
    validate_group_header(batch, Some(negotiated_limits))
}

fn validate_group_header(
    batch: &v1::CommittedGroupBatch,
    negotiated_limits: Option<&v1::TransferLimits>,
) -> Result<(), ProtocolError> {
    validate_group_identity(batch)?;
    validate_group_sizes(batch, negotiated_limits)
}

fn validate_group_identity(batch: &v1::CommittedGroupBatch) -> Result<(), ProtocolError> {
    if batch.protocol_major != V1_PROTOCOL_MAJOR {
        return Err(ProtocolError::Invalid("unsupported protocol major"));
    }
    if batch.event_format_version != V1_EVENT_FORMAT {
        return Err(ProtocolError::Invalid("unsupported event format version"));
    }
    if batch.record_format_version == 0 || batch.merge_policy_version == 0 {
        return Err(ProtocolError::Invalid(
            "unspecified format or merge version",
        ));
    }
    if batch.membership_epoch == 0 {
        return Err(ProtocolError::Invalid("zero membership epoch"));
    }
    if batch.configuration_id.len() != 32 || batch.configuration_id.iter().all(|byte| *byte == 0) {
        return Err(ProtocolError::Invalid("configuration identity"));
    }
    let event_id = batch
        .event_id
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing committed event identity"))?;
    if event_id.database_id.len() != 16 || event_id.sequence == 0 {
        return Err(ProtocolError::Invalid("database or event sequence"));
    }
    let origin = event_id
        .origin
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing origin identity"))?;
    if origin.replica_id.len() != 16
        || origin.incarnation.len() != 16
        || origin.incarnation.iter().all(|byte| *byte == 0)
    {
        return Err(ProtocolError::Invalid("origin identity"));
    }
    if batch.transaction_id.len() != 24 {
        return Err(ProtocolError::Invalid("transaction identity"));
    }
    if batch.event_digest_sha256.len() != 32 {
        return Err(ProtocolError::Invalid("event SHA-256 digest length"));
    }
    Ok(())
}

fn validate_group_sizes(
    batch: &v1::CommittedGroupBatch,
    negotiated_limits: Option<&v1::TransferLimits>,
) -> Result<(), ProtocolError> {
    let group_bytes = usize::try_from(batch.wal_group_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("WAL group bytes"))?;
    if group_bytes == 0 || group_bytes > MAX_TRANSACTION_GROUP_BYTES {
        return Err(ProtocolError::LimitExceeded("WAL group bytes"));
    }
    let metadata_bytes = usize::try_from(batch.event_metadata_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("event metadata bytes"))?;
    if metadata_bytes == 0 || metadata_bytes > MAX_EVENT_METADATA_BYTES {
        return Err(ProtocolError::LimitExceeded("event metadata bytes"));
    }
    if batch.change_count == 0
        || usize::try_from(batch.change_count)
            .map_err(|_| ProtocolError::LimitExceeded("change count"))?
            > crate::MAX_CHANGES_PER_GROUP
    {
        return Err(ProtocolError::LimitExceeded(
            "changes per transaction group",
        ));
    }

    let encoded_bytes = usize::try_from(batch.encoded_application_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("encoded application bytes"))?;
    let decoded_bytes = usize::try_from(batch.decoded_application_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("decoded application bytes"))?;
    validate_encoded_application_len(encoded_bytes)?;
    validate_application_len(decoded_bytes)?;
    let expected_decoded_bytes = metadata_bytes
        .checked_add(group_bytes)
        .ok_or(ProtocolError::LimitExceeded("decoded application bytes"))?;
    if decoded_bytes != expected_decoded_bytes {
        return Err(ProtocolError::Invalid(
            "decoded application length differs from its declaration",
        ));
    }
    if negotiated_limits.is_some_and(|limits| {
        encoded_bytes > limits.max_encoded_application_bytes as usize
            || decoded_bytes > limits.max_decoded_application_bytes as usize
            || group_bytes > limits.max_transaction_group_bytes as usize
            || metadata_bytes > limits.max_event_metadata_bytes as usize
            || batch.change_count > limits.max_changes_per_group
    }) {
        return Err(ProtocolError::LimitExceeded("negotiated group limits"));
    }
    let encoded_actual = batch
        .application_envelope
        .len()
        .checked_add(batch.distributed_metadata.len())
        .ok_or(ProtocolError::LimitExceeded("encoded application bytes"))?;
    if encoded_actual != encoded_bytes {
        return Err(ProtocolError::Invalid(
            "encoded application length differs from its declaration",
        ));
    }
    if batch.distributed_metadata.len() != metadata_bytes {
        return Err(ProtocolError::Invalid(
            "metadata length differs from its declaration",
        ));
    }
    if batch.encoded_len()
        > negotiated_limits.map_or(MAX_GRPC_MESSAGE_BYTES, |limits| {
            limits.max_grpc_message_bytes as usize
        })
    {
        return Err(ProtocolError::LimitExceeded("gRPC message bytes"));
    }
    codec_from_wire(batch.codec)?;
    Ok(())
}

/// Validates and temporarily owns transport bytes, then rejects metadata until
/// its canonical event decoder and WAL/document mapper are registered. The
/// decoded group and its process/per-peer reservations are dropped before this
/// function returns. This boundary never issues an ACK.
///
/// # Errors
///
/// Returns [`ProtocolError::UnsupportedMetadataEncoding`] after transport
/// validation because this crate has no canonical metadata decoder, or another
/// [`ProtocolError`] when the header, negotiated codec set, envelope, WAL
/// frame, digest, per-peer budget, or process budget is invalid. The caller
/// must bound concurrent Protobuf decoding before this function is called and
/// pass the negotiated codec allow-set from Welcome.
pub fn decode_committed_group(
    batch: v1::CommittedGroupBatch,
    admission: &AdmissionController,
    process_budget: &MemoryBudget,
    attribution: Attribution,
    negotiated_codecs: &[PayloadCodec],
    negotiated_limits: &v1::TransferLimits,
) -> Result<(), ProtocolError> {
    let _decoded = decode_transport_group(
        batch,
        admission,
        process_budget,
        attribution,
        negotiated_codecs,
        negotiated_limits,
    )?;
    Err(ProtocolError::UnsupportedMetadataEncoding)
}

/// Decode one transport group while retaining its exact bytes and admissions.
///
/// The returned object owns a compact copy of every Protobuf byte field, a
/// complete owned WAL frame, per-peer byte/batch admission, and process
/// memory/item reservations. Before copying or expanding, the process budget
/// is reserved for an estimate based on the serialized logical input size, all
/// compact retained byte copies, the inspected plaintext length, and the
/// fixed owned-container size.
/// The predecoded input message is already materialized by the caller. The
/// caller must separately bound/admit its backing allocation and concurrent
/// Protobuf decoding: a sliced `Bytes` value can retain a larger allocation
/// whose capacity this function cannot infer. It must also bound gRPC message
/// sizes before invoking this function.
/// Transport identity, epoch, configuration, and version declarations remain
/// claims until the semantic verifier and certified membership authority check
/// them. This function does not prove event meaning or authorize an ACK.
///
/// # Errors
/// Rejects invalid declarations, unnegotiated codecs, malformed envelopes or
/// WAL frames, digest mismatches, and per-peer or process admission failures.
pub fn decode_transport_group(
    batch: v1::CommittedGroupBatch,
    admission: &AdmissionController,
    process_budget: &MemoryBudget,
    attribution: Attribution,
    negotiated_codecs: &[PayloadCodec],
    negotiated_limits: &v1::TransferLimits,
) -> Result<DecodedTransportGroup, ProtocolError> {
    decode_transport_group_inner(
        batch,
        TransportDecodeContext {
            peer_admission: PeerAdmissionSource::Controller(admission),
            process_budget,
            attribution,
            negotiated_codecs,
            negotiated_limits,
        },
        None,
    )
}

/// Decodes a committed group whose Protobuf bytes and per-peer budget were
/// admitted by [`crate::SyncCodec`] before Prost decoding.
///
/// The consumed token owns the exact batch, one peer `BatchPermit`, and the
/// pre-decode process `WorkReservation`. The returned group retains both that
/// reservation and the separate process reservation for transport validation
/// and compact copies. This deliberately accounts for overlapping stages.
///
/// # Errors
///
/// Rejects invalid declarations, unnegotiated codecs, malformed envelopes or
/// WAL frames, digest mismatches, and process admission failures. On every
/// error the consumed peer and pre-decode reservations are dropped.
pub fn decode_transport_group_pre_admitted(
    admitted: PreAdmittedBatch,
    process_budget: &MemoryBudget,
    attribution: Attribution,
    negotiated_codecs: &[PayloadCodec],
    negotiated_limits: &v1::TransferLimits,
) -> Result<DecodedTransportGroup, ProtocolError> {
    let PreAdmittedBatch {
        batch,
        peer_permit,
        predecode_work,
    } = admitted;
    decode_transport_group_inner(
        batch,
        TransportDecodeContext {
            peer_admission: PeerAdmissionSource::PreAdmitted(peer_permit),
            process_budget,
            attribution,
            negotiated_codecs,
            negotiated_limits,
        },
        Some(predecode_work),
    )
}

struct TransportDecodeContext<'a> {
    peer_admission: PeerAdmissionSource<'a>,
    process_budget: &'a MemoryBudget,
    attribution: Attribution,
    negotiated_codecs: &'a [PayloadCodec],
    negotiated_limits: &'a v1::TransferLimits,
}

enum PeerAdmissionSource<'a> {
    Controller(&'a AdmissionController),
    PreAdmitted(BatchPermit),
}

#[derive(Clone, Copy)]
struct TransportEnvelopeInfo {
    codec: EnvelopeCodec,
    plain_len: usize,
}

struct DecodedTransportPayload {
    transaction_id: TransactionId,
    codec: EnvelopeCodec,
    digest: [u8; 32],
    wal_frame: Bytes,
}

fn decode_transport_group_inner(
    batch: v1::CommittedGroupBatch,
    context: TransportDecodeContext<'_>,
    predecode_work: Option<WorkReservation>,
) -> Result<DecodedTransportGroup, ProtocolError> {
    let TransportDecodeContext {
        peer_admission,
        process_budget,
        attribution,
        negotiated_codecs,
        negotiated_limits,
    } = context;
    let envelope_info = inspect_transport_envelope(&batch, negotiated_codecs, negotiated_limits)?;
    let encoded_bytes = usize::try_from(batch.encoded_application_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("encoded application bytes"))?;
    let decoded_bytes = usize::try_from(batch.decoded_application_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("decoded application bytes"))?;
    let reservation = encoded_bytes
        .checked_add(decoded_bytes)
        .ok_or(ProtocolError::LimitExceeded("receive reservation"))?;
    let peer_permit = match peer_admission {
        PeerAdmissionSource::Controller(admission) => admission.try_admit_batch(reservation)?,
        PeerAdmissionSource::PreAdmitted(peer_permit) if peer_permit.bytes() == reservation => {
            peer_permit
        }
        PeerAdmissionSource::PreAdmitted(_) => {
            return Err(ProtocolError::Invalid(
                "pre-admitted peer bytes differ from the decoded batch",
            ));
        }
    };
    let retained_bytes = retained_protobuf_byte_len(&batch)?;
    let process_bytes = batch
        .encoded_len()
        .checked_add(retained_bytes)
        .and_then(|bytes| bytes.checked_add(envelope_info.plain_len))
        .and_then(|bytes| bytes.checked_add(std::mem::size_of::<DecodedTransportGroup>()))
        .ok_or(ProtocolError::LimitExceeded("process receive reservation"))?;
    let process_work = process_budget.try_reserve_work(attribution, process_bytes, 1)?;
    let (process_memory, process_item) = process_work.into_parts();

    // Replace every retained Protobuf Bytes slice with an exact owned copy.
    // The returned group no longer retains oversized shared backing allocations.
    let batch = compact_protobuf_bytes(batch);
    let limits = transport_format_limits(&batch, negotiated_limits);
    let payload = decode_compacted_transport_payload(&batch, &limits, envelope_info)?;
    Ok(DecodedTransportGroup {
        batch,
        wal_frame: payload.wal_frame,
        digest: payload.digest,
        transaction_id: payload.transaction_id,
        codec: codec_to_wire(payload.codec),
        process_bytes,
        _peer_permit: peer_permit,
        _predecode_work: predecode_work,
        _process_memory: process_memory,
        _process_item: process_item,
    })
}

fn inspect_transport_envelope(
    batch: &v1::CommittedGroupBatch,
    negotiated_codecs: &[PayloadCodec],
    negotiated_limits: &v1::TransferLimits,
) -> Result<TransportEnvelopeInfo, ProtocolError> {
    validate_negotiated_codecs(negotiated_codecs)?;
    validate_committed_group_header_with_limits(batch, negotiated_limits)?;
    let declared_codec = codec_from_wire(batch.codec)?;
    if !negotiated_codecs.contains(&declared_codec) {
        return Err(ProtocolError::CodecNotNegotiated);
    }
    let limits = transport_format_limits(batch, negotiated_limits);
    let envelope = inspect_envelope(batch.application_envelope.as_ref(), &limits)?;
    if codec_to_wire(envelope.codec()) as i32 != batch.codec {
        return Err(ProtocolError::CodecMismatch);
    }
    let plain_len = envelope.plain_len();
    if plain_len != batch.wal_group_bytes as usize {
        return Err(ProtocolError::Invalid(
            "WAL frame length differs from its declaration",
        ));
    }
    let decoded_bytes = usize::try_from(batch.decoded_application_bytes)
        .map_err(|_| ProtocolError::LimitExceeded("decoded application bytes"))?;
    let expected_decoded = batch
        .distributed_metadata
        .len()
        .checked_add(plain_len)
        .ok_or(ProtocolError::LimitExceeded("decoded application bytes"))?;
    if expected_decoded != decoded_bytes {
        return Err(ProtocolError::Invalid(
            "decoded application length differs from its declaration",
        ));
    }
    Ok(TransportEnvelopeInfo {
        codec: envelope.codec(),
        plain_len,
    })
}

fn transport_format_limits(
    batch: &v1::CommittedGroupBatch,
    negotiated_limits: &v1::TransferLimits,
) -> FormatLimits {
    let mut limits = format_limits(negotiated_limits);
    limits.max_envelope_encoded_bytes = (negotiated_limits.max_encoded_application_bytes as usize)
        .saturating_sub(batch.distributed_metadata.len());
    limits
}

fn decode_compacted_transport_payload(
    batch: &v1::CommittedGroupBatch,
    limits: &FormatLimits,
    envelope_info: TransportEnvelopeInfo,
) -> Result<DecodedTransportPayload, ProtocolError> {
    let mut decoded_scratch = Vec::new();
    let transaction_id = TransactionId::from_bytes(
        batch
            .transaction_id
            .as_ref()
            .try_into()
            .map_err(|_| ProtocolError::Invalid("transaction identity"))?,
    );
    let (codec, digest, identity_frame) = {
        let envelope = decode_envelope(
            batch.application_envelope.as_ref(),
            &mut decoded_scratch,
            limits,
        )?;
        if envelope.codec() != envelope_info.codec
            || codec_to_wire(envelope.codec()) as i32 != batch.codec
        {
            return Err(ProtocolError::CodecMismatch);
        }
        if envelope.plain_bytes().len() != envelope_info.plain_len
            || envelope.plain_bytes().len() != batch.wal_group_bytes as usize
        {
            return Err(ProtocolError::Invalid(
                "WAL frame length differs from its declaration",
            ));
        }
        let wal = decode_wal_frame(envelope.plain_bytes(), limits)?;
        if wal.transaction_id() != transaction_id {
            return Err(ProtocolError::TransactionMismatch);
        }
        let digest = event_digest_sha256(&batch.distributed_metadata, envelope.plain_bytes())?;
        if digest.as_ref() != batch.event_digest_sha256.as_ref() {
            return Err(ProtocolError::EventDigestMismatch);
        }
        let codec = envelope.codec();
        let identity_frame = (codec == EnvelopeCodec::Identity)
            .then(|| Bytes::copy_from_slice(envelope.plain_bytes()));
        (codec, digest, identity_frame)
    };
    // LZ4 output is moved without a second full-frame allocation. Identity
    // envelopes borrow their WAL from the retained message, so they need the
    // explicitly reserved copy made above.
    let wal_frame = identity_frame.unwrap_or_else(|| Bytes::from(decoded_scratch));
    Ok(DecodedTransportPayload {
        transaction_id,
        codec,
        digest,
        wal_frame,
    })
}

fn retained_protobuf_byte_len(batch: &v1::CommittedGroupBatch) -> Result<usize, ProtocolError> {
    let event_id = batch
        .event_id
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing committed event identity"))?;
    let origin = event_id
        .origin
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing origin identity"))?;
    [
        batch.configuration_id.len(),
        event_id.database_id.len(),
        origin.replica_id.len(),
        origin.incarnation.len(),
        batch.transaction_id.len(),
        batch.event_digest_sha256.len(),
        batch.application_envelope.len(),
        batch.distributed_metadata.len(),
    ]
    .into_iter()
    .try_fold(0usize, |total, bytes| {
        total
            .checked_add(bytes)
            .ok_or(ProtocolError::LimitExceeded("process receive reservation"))
    })
}

fn compact_protobuf_bytes(mut batch: v1::CommittedGroupBatch) -> v1::CommittedGroupBatch {
    batch.configuration_id = Bytes::copy_from_slice(&batch.configuration_id);
    if let Some(event_id) = batch.event_id.as_mut() {
        event_id.database_id = Bytes::copy_from_slice(&event_id.database_id);
        if let Some(origin) = event_id.origin.as_mut() {
            origin.replica_id = Bytes::copy_from_slice(&origin.replica_id);
            origin.incarnation = Bytes::copy_from_slice(&origin.incarnation);
        }
    }
    batch.transaction_id = Bytes::copy_from_slice(&batch.transaction_id);
    batch.event_digest_sha256 = Bytes::copy_from_slice(&batch.event_digest_sha256);
    batch.application_envelope = Bytes::copy_from_slice(&batch.application_envelope);
    batch.distributed_metadata = Bytes::copy_from_slice(&batch.distributed_metadata);
    batch
}

fn format_limits(negotiated_limits: &v1::TransferLimits) -> FormatLimits {
    let mut limits = FormatLimits::local_v1();
    limits.max_frame_bytes = negotiated_limits.max_transaction_group_bytes as usize;
    limits.max_operations = crate::MAX_WAL_OPERATIONS_PER_GROUP;
    limits.max_envelope_encoded_bytes = negotiated_limits.max_encoded_application_bytes as usize;
    limits.max_envelope_plain_bytes = negotiated_limits.max_transaction_group_bytes as usize;
    limits
}

fn validate_metadata(metadata: &CommittedGroupMetadata) -> Result<(), ProtocolError> {
    if metadata.event_format_version != V1_EVENT_FORMAT
        || metadata.record_format_version == 0
        || metadata.merge_policy_version == 0
        || metadata.membership_epoch == 0
    {
        return Err(ProtocolError::Invalid("event version or membership epoch"));
    }
    if metadata.configuration_id.len() != 32
        || metadata.configuration_id.iter().all(|byte| *byte == 0)
    {
        return Err(ProtocolError::Invalid("configuration identity"));
    }
    validate_event_id(&metadata.event_id)?;
    if metadata.transaction_id.len() != 24 {
        return Err(ProtocolError::Invalid("transaction identity"));
    }
    if metadata.distributed_metadata.is_empty()
        || metadata.distributed_metadata.len() > MAX_EVENT_METADATA_BYTES
    {
        return Err(ProtocolError::LimitExceeded("event metadata bytes"));
    }
    if metadata.change_count == 0 || metadata.change_count > crate::MAX_CHANGES_PER_GROUP {
        return Err(ProtocolError::LimitExceeded(
            "changes per transaction group",
        ));
    }
    Ok(())
}

fn event_digest_sha256(metadata: &[u8], wal_frame: &[u8]) -> Result<[u8; 32], ProtocolError> {
    let metadata_len = u32::try_from(metadata.len())
        .map_err(|_| ProtocolError::LimitExceeded("event metadata bytes"))?;
    let frame_len = u32::try_from(wal_frame.len())
        .map_err(|_| ProtocolError::LimitExceeded("WAL group bytes"))?;
    let mut hasher = Sha256::new();
    hasher.update(b"TBREPL-EVENT-V1\0");
    hasher.update(metadata_len.to_le_bytes());
    hasher.update(metadata);
    hasher.update(frame_len.to_le_bytes());
    hasher.update(wal_frame);
    Ok(hasher.finalize().into())
}

fn validate_event_id(event_id: &v1::CommittedEventId) -> Result<(), ProtocolError> {
    if event_id.database_id.len() != 16 || event_id.sequence == 0 {
        return Err(ProtocolError::Invalid("database or event sequence"));
    }
    let origin = event_id
        .origin
        .as_ref()
        .ok_or(ProtocolError::Invalid("missing origin identity"))?;
    if origin.replica_id.len() != 16
        || origin.incarnation.len() != 16
        || origin.incarnation.iter().all(|byte| *byte == 0)
    {
        return Err(ProtocolError::Invalid("origin identity"));
    }
    Ok(())
}

fn validate_application_len(bytes: usize) -> Result<(), ProtocolError> {
    if bytes == 0 || bytes > MAX_DECODED_APPLICATION_BYTES {
        return Err(ProtocolError::LimitExceeded("application bytes"));
    }
    Ok(())
}

fn validate_encoded_application_len(bytes: usize) -> Result<(), ProtocolError> {
    if bytes == 0 || bytes > MAX_ENCODED_APPLICATION_BYTES {
        return Err(ProtocolError::LimitExceeded("encoded application bytes"));
    }
    Ok(())
}

fn codec_to_wire(codec: EnvelopeCodec) -> PayloadCodec {
    match codec {
        EnvelopeCodec::Identity => PayloadCodec::Identity,
        EnvelopeCodec::Lz4 => PayloadCodec::Lz4BlockV1,
    }
}

fn codec_from_wire(codec: i32) -> Result<PayloadCodec, ProtocolError> {
    let codec = PayloadCodec::try_from(codec)
        .map_err(|_| ProtocolError::Invalid("unknown application codec"))?;
    match codec {
        PayloadCodec::Identity | PayloadCodec::Lz4BlockV1 => Ok(codec),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AdmissionLimits, v1_transfer_limits};
    use tb_core::{BudgetConfig, MemoryCategory, TenantId, WorkerId};
    use tb_format::{FormatLimits, WalOperation, encode_wal_frame_into};

    fn metadata() -> CommittedGroupMetadata {
        CommittedGroupMetadata {
            event_id: v1::CommittedEventId {
                database_id: Bytes::copy_from_slice(&[1; 16]),
                shard_id: 7,
                origin: Some(v1::OriginIdentity {
                    replica_id: Bytes::copy_from_slice(&[2; 16]),
                    incarnation: Bytes::copy_from_slice(&[3; 16]),
                }),
                sequence: 9,
            },
            membership_epoch: 4,
            configuration_id: Bytes::copy_from_slice(&[4; 32]),
            transaction_id: Bytes::copy_from_slice(&[5; 24]),
            event_format_version: V1_EVENT_FORMAT,
            record_format_version: 1,
            merge_policy_version: 1,
            distributed_metadata: Bytes::from_static(b"opaque event metadata"),
            change_count: 2,
        }
    }

    fn wal_frame() -> Vec<u8> {
        let transaction_id = TransactionId::from_bytes([5; 24]);
        let mut output = Vec::new();
        encode_wal_frame_into(
            &mut output,
            12,
            transaction_id,
            &[
                WalOperation::Put {
                    key: b"document/1",
                    value: b"value one",
                },
                WalOperation::Delete { key: b"document/2" },
            ],
            &FormatLimits::local_v1(),
        )
        .expect("encode complete local WAL frame");
        output
    }

    fn limits() -> v1::TransferLimits {
        v1_transfer_limits(8 * 1024 * 1024, 4, 2).expect("valid v1 limits")
    }

    fn both_codecs() -> [PayloadCodec; 2] {
        [PayloadCodec::Identity, PayloadCodec::Lz4BlockV1]
    }

    fn admission() -> AdmissionController {
        AdmissionController::new(
            AdmissionLimits::new(8 * 1024 * 1024, 4, 2).expect("valid admission limits"),
        )
    }

    fn process_budget(bytes: usize) -> MemoryBudget {
        MemoryBudget::new(BudgetConfig::new(bytes, 0).expect("valid process budget"))
    }

    fn place_bytes(backing: &mut [u8], cursor: &mut usize, bytes: &[u8]) -> std::ops::Range<usize> {
        let start = *cursor;
        let end = start + bytes.len();
        backing[start..end].copy_from_slice(bytes);
        *cursor = end;
        start..end
    }

    fn points_into(bytes: &[u8], backing: &Bytes) -> bool {
        let address = bytes.as_ptr() as usize;
        let start = backing.as_ptr() as usize;
        address >= start && address < start.saturating_add(backing.len())
    }

    fn share_batch_byte_fields(batch: &mut v1::CommittedGroupBatch) -> Bytes {
        let mut oversized_storage = vec![0; 1024 * 1024];
        let mut cursor = 32;
        let configuration_id =
            place_bytes(&mut oversized_storage, &mut cursor, &batch.configuration_id);
        let event_id = batch.event_id.as_mut().expect("encoded event identity");
        let database_id = place_bytes(&mut oversized_storage, &mut cursor, &event_id.database_id);
        let origin = event_id.origin.as_mut().expect("encoded origin identity");
        let replica_id = place_bytes(&mut oversized_storage, &mut cursor, &origin.replica_id);
        let incarnation = place_bytes(&mut oversized_storage, &mut cursor, &origin.incarnation);
        let transaction_id =
            place_bytes(&mut oversized_storage, &mut cursor, &batch.transaction_id);
        let digest = place_bytes(
            &mut oversized_storage,
            &mut cursor,
            &batch.event_digest_sha256,
        );
        let envelope = place_bytes(
            &mut oversized_storage,
            &mut cursor,
            &batch.application_envelope,
        );
        let metadata_range = place_bytes(
            &mut oversized_storage,
            &mut cursor,
            &batch.distributed_metadata,
        );
        let backing = Bytes::from(oversized_storage);
        batch.configuration_id = backing.slice(configuration_id);
        let event_id = batch.event_id.as_mut().expect("encoded event identity");
        event_id.database_id = backing.slice(database_id);
        let origin = event_id.origin.as_mut().expect("encoded origin identity");
        origin.replica_id = backing.slice(replica_id);
        origin.incarnation = backing.slice(incarnation);
        batch.transaction_id = backing.slice(transaction_id);
        batch.event_digest_sha256 = backing.slice(digest);
        batch.application_envelope = backing.slice(envelope);
        batch.distributed_metadata = backing.slice(metadata_range);
        backing
    }

    fn assert_batch_byte_fields_backed_by(
        batch: &v1::CommittedGroupBatch,
        backing: &Bytes,
        expected: bool,
    ) {
        let event_id = batch.event_id.as_ref().expect("event identity");
        let origin = event_id.origin.as_ref().expect("origin identity");
        for bytes in [
            batch.configuration_id.as_ref(),
            event_id.database_id.as_ref(),
            origin.replica_id.as_ref(),
            origin.incarnation.as_ref(),
            batch.transaction_id.as_ref(),
            batch.event_digest_sha256.as_ref(),
            batch.application_envelope.as_ref(),
            batch.distributed_metadata.as_ref(),
        ] {
            assert_eq!(points_into(bytes, backing), expected);
        }
    }

    fn attribution() -> Attribution {
        Attribution {
            tenant: TenantId::from_bytes([7; 16]),
            worker: WorkerId(9),
            category: MemoryCategory::Transfer,
        }
    }

    #[test]
    fn complete_group_round_trips_with_raii_admission() {
        let sender = admission();
        let receiver = admission();
        let budget = process_budget(16 * 1024 * 1024);
        let frame = wal_frame();
        let metadata_bytes = metadata().distributed_metadata.len();
        let mut output = Vec::new();
        let encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut output,
            &sender,
            &both_codecs(),
            &limits(),
        )
        .expect("encode complete group");
        let (batch, send_permit) = encoded.into_parts();
        assert_eq!(sender.in_flight_batches(), 1);
        assert_eq!(
            batch.event_digest_sha256.as_ref(),
            event_digest_sha256(&metadata().distributed_metadata, &frame)
                .expect("event digest fits the bounded payload")
                .as_ref()
        );
        assert_eq!(
            batch.encoded_application_bytes as usize,
            batch.application_envelope.len() + metadata_bytes
        );
        assert_eq!(
            batch.decoded_application_bytes as usize,
            frame.len() + metadata_bytes
        );

        drop(send_permit);
        let decoded = decode_transport_group(
            batch,
            &receiver,
            &budget,
            attribution(),
            &both_codecs(),
            &limits(),
        )
        .expect("validate transport envelope");
        assert!(matches!(
            decoded.codec(),
            PayloadCodec::Identity | PayloadCodec::Lz4BlockV1
        ));
        assert_eq!(
            decoded.metadata_bytes(),
            metadata().distributed_metadata.as_ref()
        );
        assert_eq!(decoded.wal_frame_bytes(), frame.as_slice());
        assert_eq!(
            decoded.event_digest(),
            event_digest_sha256(decoded.metadata_bytes(), decoded.wal_frame_bytes())
                .expect("event digest fits the bounded payload")
        );
        assert_eq!(decoded.transaction_id(), TransactionId::from_bytes([5; 24]));
        assert_eq!(
            decoded.event_id_claim().map(|event| event.sequence),
            Some(9)
        );
        assert_eq!(receiver.in_flight_batches(), 1);
        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_bytes, decoded.process_reservation_bytes());
        assert_eq!(snapshot.reserved_items, 1);
        drop(decoded);
        assert_eq!(receiver.in_flight_batches(), 0);
        assert_eq!(receiver.reserved_bytes(), 0);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }

    #[test]
    fn receive_compacts_every_protobuf_byte_field_and_charges_the_copy_peak() {
        let sender = admission();
        let receiver = admission();
        let budget = process_budget(16 * 1024 * 1024);
        let frame = wal_frame();
        let mut output = Vec::new();
        let identity_only = [PayloadCodec::Identity];
        let encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut output,
            &sender,
            &identity_only,
            &limits(),
        )
        .expect("encode complete identity group");
        let (mut batch, send_permit) = encoded.into_parts();
        drop(send_permit);
        let oversized_backing = share_batch_byte_fields(&mut batch);
        assert_batch_byte_fields_backed_by(&batch, &oversized_backing, true);

        let expected_process_charge = batch.encoded_len()
            + retained_protobuf_byte_len(&batch).expect("bounded byte lengths sum")
            + frame.len()
            + std::mem::size_of::<DecodedTransportGroup>();
        let decoded = decode_transport_group(
            batch,
            &receiver,
            &budget,
            attribution(),
            &identity_only,
            &limits(),
        )
        .expect("decode and compact transport byte fields");

        assert_eq!(
            decoded.metadata_bytes(),
            metadata().distributed_metadata.as_ref()
        );
        assert_eq!(decoded.wal_frame_bytes(), frame.as_slice());
        assert_eq!(decoded.process_reservation_bytes(), expected_process_charge);
        assert!(!points_into(decoded.metadata_bytes(), &oversized_backing));
        assert_batch_byte_fields_backed_by(&decoded.batch, &oversized_backing, false);
    }

    #[test]
    fn declared_decoded_size_and_digest_fail_without_leaking_credits() {
        let controller = admission();
        let receiver = admission();
        let budget = process_budget(16 * 1024 * 1024);
        let frame = wal_frame();
        let mut output = Vec::new();
        let encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut output,
            &controller,
            &both_codecs(),
            &limits(),
        )
        .expect("encode complete group");
        let (mut batch, send_permit) = encoded.into_parts();
        batch.decoded_application_bytes = u32::try_from(MAX_DECODED_APPLICATION_BYTES + 1)
            .expect("v1 decoded application ceiling fits the protobuf field");
        assert!(matches!(
            decode_committed_group(
                batch.clone(),
                &receiver,
                &budget,
                attribution(),
                &both_codecs(),
                &limits()
            ),
            Err(ProtocolError::LimitExceeded("application bytes"))
        ));
        assert_eq!(controller.in_flight_batches(), 1);

        batch.decoded_application_bytes =
            u32::try_from(frame.len() + batch.distributed_metadata.len())
                .expect("test application size fits the protobuf field");
        batch.event_digest_sha256 = Bytes::copy_from_slice(&[0; 32]);
        assert!(matches!(
            decode_transport_group(
                batch,
                &receiver,
                &budget,
                attribution(),
                &both_codecs(),
                &limits()
            ),
            Err(ProtocolError::EventDigestMismatch)
        ));
        assert_eq!(controller.in_flight_batches(), 1);
        drop(send_permit);
        assert_eq!(controller.reserved_bytes(), 0);
        assert_eq!(controller.in_flight_batches(), 0);
        assert_eq!(receiver.reserved_bytes(), 0);
        assert_eq!(receiver.in_flight_batches(), 0);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }

    #[test]
    fn decoded_size_mismatch_is_rejected_before_admission_or_envelope_decode() {
        let sender = admission();
        let receiver = AdmissionController::new(
            AdmissionLimits::new(1, 1, 1).expect("valid small admission limits"),
        );
        let budget = process_budget(16 * 1024 * 1024);
        let frame = wal_frame();
        let mut output = Vec::new();
        let encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut output,
            &sender,
            &both_codecs(),
            &limits(),
        )
        .expect("encode complete group");
        let (mut batch, send_permit) = encoded.into_parts();
        batch.decoded_application_bytes = batch.event_metadata_bytes;

        assert!(matches!(
            validate_committed_group_header_with_limits(&batch, &limits()),
            Err(ProtocolError::Invalid(
                "decoded application length differs from its declaration"
            ))
        ));
        assert!(matches!(
            decode_transport_group(
                batch.clone(),
                &receiver,
                &budget,
                attribution(),
                &both_codecs(),
                &limits()
            ),
            Err(ProtocolError::Invalid(
                "decoded application length differs from its declaration"
            ))
        ));

        batch.wal_group_bytes = 1;
        batch.decoded_application_bytes = batch
            .event_metadata_bytes
            .checked_add(1)
            .expect("test application size fits the protobuf field");
        validate_committed_group_header_with_limits(&batch, &limits())
            .expect("declared lengths are internally consistent");
        assert!(matches!(
            decode_transport_group(
                batch,
                &receiver,
                &budget,
                attribution(),
                &both_codecs(),
                &limits()
            ),
            Err(ProtocolError::Invalid(
                "WAL frame length differs from its declaration"
            ))
        ));
        assert_eq!(receiver.in_flight_batches(), 0);
        assert_eq!(receiver.reserved_bytes(), 0);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
        drop(send_permit);
        assert_eq!(sender.reserved_bytes(), 0);
    }

    #[test]
    fn encoder_and_receiver_enforce_negotiated_codec_allow_sets() {
        let frame = wal_frame();
        let identity_sender = admission();
        let identity_receiver = admission();
        let identity_budget = process_budget(16 * 1024 * 1024);
        let mut identity_output = Vec::new();
        let identity_only = [PayloadCodec::Identity];
        let identity_encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut identity_output,
            &identity_sender,
            &identity_only,
            &limits(),
        )
        .expect("identity-only peer accepts the identity envelope");
        let (identity_batch, identity_send_permit) = identity_encoded.into_parts();
        assert_eq!(identity_batch.codec, PayloadCodec::Identity as i32);
        drop(identity_send_permit);
        let identity_decoded = decode_transport_group(
            identity_batch,
            &identity_receiver,
            &identity_budget,
            attribution(),
            &identity_only,
            &limits(),
        )
        .expect("identity is negotiated");
        assert_eq!(identity_decoded.codec(), PayloadCodec::Identity);
        assert_eq!(identity_decoded.wal_frame_bytes(), frame.as_slice());
        drop(identity_decoded);
        assert_eq!(identity_budget.snapshot().reserved_bytes, 0);

        let lz4_sender = admission();
        let lz4_receiver = admission();
        let lz4_budget = process_budget(16 * 1024 * 1024);
        let mut lz4_output = Vec::new();
        let lz4_only = [PayloadCodec::Lz4BlockV1];
        let lz4_encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut lz4_output,
            &lz4_sender,
            &lz4_only,
            &limits(),
        )
        .expect("LZ4-only peer accepts the LZ4 envelope");
        let (lz4_batch, lz4_send_permit) = lz4_encoded.into_parts();
        assert_eq!(lz4_batch.codec, PayloadCodec::Lz4BlockV1 as i32);

        let pre_admission_controller = AdmissionController::new(
            AdmissionLimits::new(1, 1, 1).expect("valid small admission limits"),
        );
        assert!(matches!(
            decode_transport_group(
                lz4_batch.clone(),
                &pre_admission_controller,
                &lz4_budget,
                attribution(),
                &identity_only,
                &limits()
            ),
            Err(ProtocolError::CodecNotNegotiated)
        ));
        assert_eq!(pre_admission_controller.in_flight_batches(), 0);
        assert_eq!(pre_admission_controller.reserved_bytes(), 0);
        assert_eq!(lz4_budget.snapshot().reserved_bytes, 0);
        assert_eq!(lz4_budget.snapshot().reserved_items, 0);
        drop(lz4_send_permit);
        let lz4_decoded = decode_transport_group(
            lz4_batch,
            &lz4_receiver,
            &lz4_budget,
            attribution(),
            &lz4_only,
            &limits(),
        )
        .expect("LZ4 is negotiated");
        assert_eq!(lz4_decoded.codec(), PayloadCodec::Lz4BlockV1);
        assert_eq!(lz4_decoded.wal_frame_bytes(), frame.as_slice());
        assert_eq!(lz4_budget.snapshot().reserved_items, 1);
        drop(lz4_decoded);
        assert_eq!(lz4_budget.snapshot().reserved_bytes, 0);
        assert_eq!(lz4_budget.snapshot().reserved_items, 0);
        assert_eq!(lz4_receiver.reserved_bytes(), 0);
        assert_eq!(lz4_receiver.in_flight_batches(), 0);
    }

    #[test]
    fn process_budget_rejects_before_decode_and_releases_peer_credit() {
        let sender = admission();
        let receiver = admission();
        let budget = process_budget(1);
        let frame = wal_frame();
        let mut output = Vec::new();
        let lz4_only = [PayloadCodec::Lz4BlockV1];
        let encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut output,
            &sender,
            &lz4_only,
            &limits(),
        )
        .expect("encode bounded LZ4 group");
        let (batch, send_permit) = encoded.into_parts();
        drop(send_permit);

        assert!(matches!(
            decode_transport_group(
                batch,
                &receiver,
                &budget,
                attribution(),
                &lz4_only,
                &limits()
            ),
            Err(ProtocolError::ProcessAdmission(_))
        ));
        assert_eq!(receiver.reserved_bytes(), 0);
        assert_eq!(receiver.in_flight_batches(), 0);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }

    #[test]
    fn public_receive_rejects_unregistered_event_metadata_after_transport_validation() {
        let sender = admission();
        let receiver = admission();
        let budget = process_budget(16 * 1024 * 1024);
        let frame = wal_frame();
        let mut output = Vec::new();
        let encoded = encode_committed_group(
            metadata(),
            &frame,
            &mut output,
            &sender,
            &both_codecs(),
            &limits(),
        )
        .expect("encode transport frame");
        let (batch, send_permit) = encoded.into_parts();
        drop(send_permit);

        assert!(matches!(
            decode_committed_group(
                batch,
                &receiver,
                &budget,
                attribution(),
                &both_codecs(),
                &limits()
            ),
            Err(ProtocolError::UnsupportedMetadataEncoding)
        ));
        assert_eq!(sender.reserved_bytes(), 0);
        assert_eq!(sender.in_flight_batches(), 0);
        assert_eq!(receiver.reserved_bytes(), 0);
        assert_eq!(receiver.in_flight_batches(), 0);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }
}
