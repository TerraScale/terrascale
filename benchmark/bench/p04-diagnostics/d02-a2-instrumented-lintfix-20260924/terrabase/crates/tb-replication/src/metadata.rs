//! Canonical distributed metadata and semantic mapping of a complete local WAL group.

use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;
use std::fmt;

use sha2::{Digest, Sha256};
use tb_core::{
    AdmissionError, Attribution, CollectionId, DocumentId, MemoryBudget, MemoryCategory, OriginId,
    ReplicaId, TransactionId, WorkReservation,
};
use tb_format::{
    DocumentEventId, DocumentKey, DocumentKind, FormatError, FormatLimits, WalOperation,
    decode_change, decode_document_key, decode_high_water_value, decode_retry_value,
    decode_state_pointer, decode_state_with_limits, decode_wal_frame,
};

use crate::event::{
    CausalContext, CollectionBinding, CommitEvent, ConfigurationId, DocumentChange, DocumentValue,
    EncodedEventSizes, EventDigest, EventHeader, EventId, EventVersions,
    MAX_DISTRIBUTED_METADATA_BYTES, MAX_EVENT_CHANGES, MAX_WAL_GROUP_BYTES, MembershipEpoch,
    ReplicationError, ReplicationLimits,
};

const MAGIC: &[u8; 8] = b"TBMET001";
const DIGEST_DOMAIN: &[u8] = b"TBREPL-EVENT-V1\0";
const HEADER_LEN: usize = 150;
const BINDING_LEN: usize = 56;
const CHANGE_LEN: usize = 23;
const CAUSAL_LEN: usize = 40;
const CRC_LEN: usize = 4;

/// The metadata fields that are known before the containing WAL frame exists.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MetadataHeader {
    pub id: EventId,
    pub epoch: MembershipEpoch,
    pub configuration: ConfigurationId,
    pub transaction_id: TransactionId,
    pub record_format: u16,
    pub merge_policy: u16,
}

/// A source change in canonical `(collection, document)` order.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MetadataChange {
    pub collection: CollectionId,
    pub document: DocumentId,
    pub source_wal_ordinal: u16,
    pub kind: DocumentKind,
    pub observed: CausalContext,
}

/// The purpose of exactly one operation in the source WAL frame.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum WalRole {
    Marker = 1,
    SourceChange = 2,
    Retry = 3,
    Primary = 4,
    LocalIndex = 5,
    HighWater = 6,
}

impl TryFrom<u8> for WalRole {
    type Error = MetadataError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            1 => Ok(Self::Marker),
            2 => Ok(Self::SourceChange),
            3 => Ok(Self::Retry),
            4 => Ok(Self::Primary),
            5 => Ok(Self::LocalIndex),
            6 => Ok(Self::HighWater),
            _ => Err(MetadataError::Invalid("unknown WAL role")),
        }
    }
}

/// Fully checked metadata, before the containing WAL operations are matched.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EventMetadata {
    pub header: MetadataHeader,
    pub bindings: Vec<CollectionBinding>,
    pub changes: Vec<MetadataChange>,
    pub roles: Vec<WalRole>,
}

/// Rejection at the canonical metadata or semantic WAL boundary.
#[derive(Debug)]
pub enum MetadataError {
    Invalid(&'static str),
    Limit(&'static str),
    Allocation,
    Checksum,
    DigestMismatch,
    Format(FormatError),
    Replication(ReplicationError),
    Admission(AdmissionError),
}

impl fmt::Display for MetadataError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{self:?}")
    }
}

impl Error for MetadataError {}

impl From<FormatError> for MetadataError {
    fn from(error: FormatError) -> Self {
        Self::Format(error)
    }
}

impl From<ReplicationError> for MetadataError {
    fn from(error: ReplicationError) -> Self {
        Self::Replication(error)
    }
}

impl From<AdmissionError> for MetadataError {
    fn from(error: AdmissionError) -> Self {
        Self::Admission(error)
    }
}

/// One semantically verified incoming event tied to its exact borrowed bytes.
/// The reservation covers semantic parsing, owned changes, and one in-flight
/// item until this proof is dropped after the journal's synchronous append.
/// This proof does not certify membership, schema availability, or an ACK.
pub struct VerifiedIncomingGroup<'a> {
    metadata: &'a [u8],
    wal_frame: &'a [u8],
    event: CommitEvent,
    source_changes: Vec<VerifiedSourceChange<'a>>,
    _semantic_permit: WorkReservation,
}

/// Exact source D1 bytes from one semantically verified complete WAL group.
/// The reference to this view is tied to its parent's semantic reservation.
pub struct VerifiedSourceChange<'a> {
    binding: CollectionBinding,
    document: DocumentId,
    kind: DocumentKind,
    logical_ordinal: u16,
    source_wal_ordinal: u16,
    key: &'a [u8],
    value: &'a [u8],
}

impl VerifiedSourceChange<'_> {
    #[must_use]
    pub fn binding(&self) -> CollectionBinding {
        self.binding
    }
    #[must_use]
    pub fn document(&self) -> DocumentId {
        self.document
    }
    #[must_use]
    pub fn kind(&self) -> DocumentKind {
        self.kind
    }
    #[must_use]
    pub fn logical_ordinal(&self) -> u16 {
        self.logical_ordinal
    }
    #[must_use]
    pub fn source_wal_ordinal(&self) -> u16 {
        self.source_wal_ordinal
    }
    #[must_use]
    pub fn key(&self) -> &[u8] {
        self.key
    }
    #[must_use]
    pub fn value(&self) -> &[u8] {
        self.value
    }
}

impl VerifiedIncomingGroup<'_> {
    /// The validated logical event, still subject to external authority checks.
    #[must_use]
    pub fn event(&self) -> &CommitEvent {
        &self.event
    }

    /// Exact immutable D1 operations in sorted logical change-table order.
    #[must_use]
    pub fn source_changes(&self) -> &[VerifiedSourceChange<'_>] {
        &self.source_changes
    }

    pub(crate) fn journal_group(&self) -> crate::journal::VerifiedJournalGroup<'_> {
        crate::journal::VerifiedJournalGroup {
            id: self.event.id(),
            epoch: self.event.epoch(),
            configuration: self.event.configuration(),
            transaction_id: self.event.transaction_id(),
            digest: self.event.digest(),
            metadata: self.metadata,
            wal_frame: self.wal_frame,
        }
    }
}

fn semantic_reservation_bytes(metadata_len: usize, wal_len: usize) -> Result<usize, MetadataError> {
    if !(HEADER_LEN + CRC_LEN..=MAX_DISTRIBUTED_METADATA_BYTES).contains(&metadata_len)
        || wal_len == 0
        || wal_len > MAX_WAL_GROUP_BYTES
    {
        return Err(MetadataError::Limit("incoming event bytes"));
    }
    // The WAL is borrowed. The factor covers source value clones, source/state
    // lookup tables, and the largest transient state decode. Metadata's factor
    // covers bounded B-tree nodes, parsed sections, and cloned causal contexts.
    wal_len
        .checked_mul(4)
        .and_then(|bytes| {
            metadata_len
                .checked_mul(16)
                .and_then(|metadata| bytes.checked_add(metadata))
        })
        .and_then(|bytes| bytes.checked_add(1024 * 1024))
        .ok_or(MetadataError::Limit("semantic reservation bytes"))
}

/// Verify exact incoming bytes under one process-budget work reservation.
/// Callers must keep their transport bytes/permit alive through this proof's
/// synchronous journal append. Membership and schema authority are separate
/// gates at the runtime boundary.
///
/// # Errors
/// Returns admission, metadata, WAL, digest, or semantic mapping failures.
pub fn verify_incoming<'a>(
    metadata: &'a [u8],
    wal_frame: &'a [u8],
    digest: EventDigest,
    limits: ReplicationLimits,
    budget: &MemoryBudget,
    mut attribution: Attribution,
) -> Result<VerifiedIncomingGroup<'a>, MetadataError> {
    let bytes = semantic_reservation_bytes(metadata.len(), wal_frame.len())?;
    attribution.category = MemoryCategory::Replication;
    let permit = budget.try_reserve_work(attribution, bytes, 1)?;
    let (event, source_changes) = verify_event_with_sources(metadata, wal_frame, digest, limits)?;
    Ok(VerifiedIncomingGroup {
        metadata,
        wal_frame,
        event,
        source_changes,
        _semantic_permit: permit,
    })
}

fn validate_header(header: MetadataHeader) -> Result<(), MetadataError> {
    if header.epoch.get() == 0 {
        return Err(MetadataError::Invalid("zero membership epoch"));
    }
    if !matches!(header.record_format, 1 | 2) || header.merge_policy != 1 {
        return Err(MetadataError::Invalid(
            "unsupported record format or merge policy",
        ));
    }
    Ok(())
}

fn validate_sections(
    header: MetadataHeader,
    bindings: &[CollectionBinding],
    changes: &[MetadataChange],
    roles: &[WalRole],
    limits: ReplicationLimits,
) -> Result<usize, MetadataError> {
    let limits = limits.validate()?;
    validate_header(header)?;
    if bindings.is_empty() || bindings.len() > MAX_EVENT_CHANGES {
        return Err(MetadataError::Limit("collection bindings"));
    }
    if changes.is_empty() || changes.len() > limits.max_changes {
        return Err(MetadataError::Limit("logical changes"));
    }
    if roles.is_empty() || roles.len() > 1_024 {
        return Err(MetadataError::Limit("WAL operation roles"));
    }
    let mut previous_collection = None;
    for binding in bindings {
        if binding.generation == 0
            || previous_collection.is_some_and(|previous| previous >= binding.collection)
        {
            return Err(MetadataError::Invalid(
                "unsorted or invalid collection binding",
            ));
        }
        previous_collection = Some(binding.collection);
    }
    let mut previous_change = None;
    let mut source_ordinals = BTreeSet::new();
    let mut used_collections = BTreeSet::new();
    let mut total_len = HEADER_LEN
        .checked_add(bindings.len() * BINDING_LEN)
        .and_then(|len| len.checked_add(roles.len()))
        .and_then(|len| len.checked_add(CRC_LEN))
        .ok_or(MetadataError::Limit("metadata length"))?;
    for change in changes {
        let binding_index = bindings
            .binary_search_by_key(&change.collection, |binding| binding.collection)
            .map_err(|_| MetadataError::Invalid("change has no collection binding"))?;
        let order = (binding_index, change.document);
        if previous_change.is_some_and(|previous| previous >= order) {
            return Err(MetadataError::Invalid(
                "unsorted or duplicate logical change",
            ));
        }
        previous_change = Some(order);
        used_collections.insert(change.collection);
        if usize::from(change.source_wal_ordinal) >= roles.len()
            || roles[usize::from(change.source_wal_ordinal)] != WalRole::SourceChange
            || !source_ordinals.insert(change.source_wal_ordinal)
        {
            return Err(MetadataError::Invalid(
                "source WAL ordinal is missing or duplicated",
            ));
        }
        if change.observed.len() > limits.max_origins.min(1_024) {
            return Err(MetadataError::Limit("causal entries"));
        }
        if change
            .observed
            .entries()
            .any(|(origin, _)| origin.incarnation == [0; 16])
        {
            return Err(MetadataError::Invalid("zero causal origin incarnation"));
        }
        if change.observed.get(header.id.origin()) != header.id.sequence() - 1 {
            return Err(MetadataError::Invalid("own causal predecessor"));
        }
        total_len = total_len
            .checked_add(CHANGE_LEN + change.observed.len() * CAUSAL_LEN)
            .ok_or(MetadataError::Limit("metadata length"))?;
    }
    if used_collections.len() != bindings.len() {
        return Err(MetadataError::Invalid("unused collection binding"));
    }
    let counts = roles.iter().fold([0_usize; 6], |mut counts, role| {
        counts[*role as usize - 1] += 1;
        counts
    });
    if counts[0] != 1
        || counts[1] != changes.len()
        || counts[2] != 1
        || counts[3] != changes.len()
        || counts[5] != 1
    {
        return Err(MetadataError::Invalid("WAL role cardinality"));
    }
    if total_len > MAX_DISTRIBUTED_METADATA_BYTES {
        return Err(MetadataError::Limit("metadata bytes"));
    }
    Ok(total_len)
}

/// Encode the canonical `TBMET001` marker value without a self-referential digest.
/// The caller keeps the memory reservation covering `output` for its lifetime.
///
/// # Errors
/// Rejects invalid order, cardinality, causal prefixes, limits, or allocation.
pub fn encode_metadata_into(
    output: &mut Vec<u8>,
    header: MetadataHeader,
    bindings: &[CollectionBinding],
    changes: &[MetadataChange],
    roles: &[WalRole],
    limits: ReplicationLimits,
) -> Result<(), MetadataError> {
    let total_len = validate_sections(header, bindings, changes, roles, limits)?;
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(total_len)
        .map_err(|_| MetadataError::Allocation)?;
    bytes.extend_from_slice(MAGIC);
    bytes.extend_from_slice(
        &u32::try_from(total_len)
            .map_err(|_| MetadataError::Limit("metadata length"))?
            .to_le_bytes(),
    );
    encode_event_id(&mut bytes, header.id);
    bytes.extend_from_slice(&header.epoch.get().to_le_bytes());
    bytes.extend_from_slice(&header.configuration.to_bytes());
    bytes.extend_from_slice(&header.transaction_id.to_bytes());
    bytes.extend_from_slice(&header.record_format.to_le_bytes());
    bytes.extend_from_slice(&header.merge_policy.to_le_bytes());
    bytes.extend_from_slice(
        &u16::try_from(bindings.len())
            .map_err(|_| MetadataError::Limit("collection bindings"))?
            .to_le_bytes(),
    );
    bytes.extend_from_slice(
        &u16::try_from(changes.len())
            .map_err(|_| MetadataError::Limit("logical changes"))?
            .to_le_bytes(),
    );
    bytes.extend_from_slice(
        &u16::try_from(roles.len())
            .map_err(|_| MetadataError::Limit("WAL operation roles"))?
            .to_le_bytes(),
    );
    for binding in bindings {
        bytes.extend_from_slice(&binding.collection.to_bytes());
        bytes.extend_from_slice(&binding.generation.to_le_bytes());
        bytes.extend_from_slice(&binding.schema_digest);
    }
    for change in changes {
        let index = bindings
            .binary_search_by_key(&change.collection, |binding| binding.collection)
            .map_err(|_| MetadataError::Invalid("change has no collection binding"))?;
        let index =
            u16::try_from(index).map_err(|_| MetadataError::Limit("collection binding index"))?;
        bytes.extend_from_slice(&index.to_le_bytes());
        bytes.extend_from_slice(&change.document.to_bytes());
        bytes.extend_from_slice(&change.source_wal_ordinal.to_le_bytes());
        bytes.push(change.kind as u8);
        bytes.extend_from_slice(
            &u16::try_from(change.observed.len())
                .map_err(|_| MetadataError::Limit("causal entries"))?
                .to_le_bytes(),
        );
        for (origin, sequence) in change.observed.entries() {
            bytes.extend_from_slice(&origin.replica_id.to_bytes());
            bytes.extend_from_slice(&origin.incarnation);
            bytes.extend_from_slice(&sequence.to_le_bytes());
        }
    }
    bytes.extend(roles.iter().map(|role| *role as u8));
    debug_assert_eq!(bytes.len() + CRC_LEN, total_len);
    let crc = crc32c::crc32c(&bytes);
    bytes.extend_from_slice(&crc.to_le_bytes());
    *output = bytes;
    Ok(())
}

struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
    end: usize,
}

impl<'a> Reader<'a> {
    fn take(&mut self, count: usize) -> Result<&'a [u8], MetadataError> {
        let end = self
            .position
            .checked_add(count)
            .filter(|&end| end <= self.end)
            .ok_or(MetadataError::Invalid("truncated metadata section"))?;
        let result = &self.bytes[self.position..end];
        self.position = end;
        Ok(result)
    }

    fn u16(&mut self) -> Result<u16, MetadataError> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap()))
    }

    fn u64(&mut self) -> Result<u64, MetadataError> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }
}

/// Decode and validate one complete `TBMET001` value.
///
/// # Errors
/// Rejects checksum, canonical ordering, cardinality, length, and version failures.
#[allow(
    clippy::missing_panics_doc,
    clippy::too_many_lines,
    reason = "fixed-width conversions follow checked frame bounds; decoding stays in wire order"
)]
pub fn decode_metadata(
    bytes: &[u8],
    limits: ReplicationLimits,
) -> Result<EventMetadata, MetadataError> {
    let limits = limits.validate()?;
    if bytes.len() < HEADER_LEN + CRC_LEN || bytes.len() > MAX_DISTRIBUTED_METADATA_BYTES {
        return Err(MetadataError::Limit("metadata bytes"));
    }
    if &bytes[..8] != MAGIC
        || u32::from_le_bytes(bytes[8..12].try_into().unwrap()) as usize != bytes.len()
    {
        return Err(MetadataError::Invalid("metadata magic or length"));
    }
    if crc32c::crc32c(&bytes[..bytes.len() - CRC_LEN])
        != u32::from_le_bytes(bytes[bytes.len() - CRC_LEN..].try_into().unwrap())
    {
        return Err(MetadataError::Checksum);
    }
    let mut reader = Reader {
        bytes,
        position: 12,
        end: bytes.len() - CRC_LEN,
    };
    let id = EventId::new(
        tb_core::DatabaseId::from_bytes(reader.take(16)?.try_into().unwrap()),
        tb_core::ShardId::new(reader.u64()?),
        OriginId {
            replica_id: ReplicaId::from_bytes(reader.take(16)?.try_into().unwrap()),
            incarnation: reader.take(16)?.try_into().unwrap(),
        },
        reader.u64()?,
    )?;
    let epoch = MembershipEpoch::new(reader.u64()?);
    let configuration = ConfigurationId::from_bytes(reader.take(32)?.try_into().unwrap())?;
    let transaction_id = TransactionId::from_bytes(reader.take(24)?.try_into().unwrap());
    let header = MetadataHeader {
        id,
        epoch,
        configuration,
        transaction_id,
        record_format: reader.u16()?,
        merge_policy: reader.u16()?,
    };
    validate_header(header)?;
    let binding_count = usize::from(reader.u16()?);
    let change_count = usize::from(reader.u16()?);
    let role_count = usize::from(reader.u16()?);
    if binding_count == 0
        || binding_count > MAX_EVENT_CHANGES
        || change_count == 0
        || change_count > limits.max_changes
        || role_count == 0
        || role_count > 1_024
    {
        return Err(MetadataError::Limit("metadata section count"));
    }
    let minimum_body = binding_count
        .checked_mul(BINDING_LEN)
        .and_then(|len| len.checked_add(change_count * CHANGE_LEN))
        .and_then(|len| len.checked_add(role_count))
        .ok_or(MetadataError::Limit("metadata section lengths"))?;
    if reader.end - reader.position < minimum_body {
        return Err(MetadataError::Invalid("truncated metadata sections"));
    }
    let mut bindings = Vec::new();
    bindings
        .try_reserve_exact(binding_count)
        .map_err(|_| MetadataError::Allocation)?;
    for _ in 0..binding_count {
        bindings.push(CollectionBinding {
            collection: CollectionId::from_bytes(reader.take(16)?.try_into().unwrap()),
            generation: reader.u64()?,
            schema_digest: reader.take(32)?.try_into().unwrap(),
        });
    }
    let mut changes = Vec::new();
    changes
        .try_reserve_exact(change_count)
        .map_err(|_| MetadataError::Allocation)?;
    for index in 0..change_count {
        let binding_index = usize::from(reader.u16()?);
        let collection = bindings
            .get(binding_index)
            .ok_or(MetadataError::Invalid("change binding index"))?
            .collection;
        let document = DocumentId::from_bytes(reader.take(16)?.try_into().unwrap());
        let source_wal_ordinal = reader.u16()?;
        let kind = DocumentKind::try_from(reader.take(1)?[0])?;
        let causal_count = usize::from(reader.u16()?);
        if causal_count > limits.max_origins.min(1_024) {
            return Err(MetadataError::Limit("causal entries"));
        }
        let required_tail = causal_count
            .checked_mul(CAUSAL_LEN)
            .and_then(|len| len.checked_add((change_count - index - 1) * CHANGE_LEN))
            .and_then(|len| len.checked_add(role_count))
            .ok_or(MetadataError::Limit("causal section length"))?;
        if reader.end - reader.position < required_tail {
            return Err(MetadataError::Invalid("truncated causal section"));
        }
        let mut entries = Vec::new();
        entries
            .try_reserve_exact(causal_count)
            .map_err(|_| MetadataError::Allocation)?;
        let mut previous = None;
        for _ in 0..causal_count {
            let origin = OriginId {
                replica_id: ReplicaId::from_bytes(reader.take(16)?.try_into().unwrap()),
                incarnation: reader.take(16)?.try_into().unwrap(),
            };
            let sequence = reader.u64()?;
            if origin.incarnation == [0; 16] || previous.is_some_and(|last| last >= origin) {
                return Err(MetadataError::Invalid(
                    "causal entries are not strictly sorted",
                ));
            }
            previous = Some(origin);
            entries.push((origin, sequence));
        }
        changes.push(MetadataChange {
            collection,
            document,
            source_wal_ordinal,
            kind,
            observed: CausalContext::from_entries(entries, limits.max_origins)?,
        });
    }
    let mut roles = Vec::new();
    roles
        .try_reserve_exact(role_count)
        .map_err(|_| MetadataError::Allocation)?;
    for &role in reader.take(role_count)? {
        roles.push(WalRole::try_from(role)?);
    }
    if reader.position != reader.end {
        return Err(MetadataError::Invalid("metadata trailing bytes"));
    }
    validate_sections(header, &bindings, &changes, &roles, limits)?;
    Ok(EventMetadata {
        header,
        bindings,
        changes,
        roles,
    })
}

fn encode_event_id(output: &mut Vec<u8>, id: EventId) {
    output.extend_from_slice(&id.database().to_bytes());
    output.extend_from_slice(&id.shard().get().to_le_bytes());
    output.extend_from_slice(&id.origin().replica_id.to_bytes());
    output.extend_from_slice(&id.origin().incarnation);
    output.extend_from_slice(&id.sequence().to_le_bytes());
}

fn document_event_id(id: EventId) -> DocumentEventId {
    DocumentEventId {
        database: id.database(),
        shard: id.shard(),
        origin: id.origin(),
        sequence: id.sequence(),
    }
}

/// Compute the sole canonical SHA-256 event digest over exact metadata and WAL bytes.
///
/// # Errors
/// Rejects bytes beyond the v1 event caps.
pub fn compute_event_digest(
    metadata: &[u8],
    wal_frame: &[u8],
) -> Result<EventDigest, MetadataError> {
    if metadata.is_empty()
        || metadata.len() > MAX_DISTRIBUTED_METADATA_BYTES
        || wal_frame.is_empty()
        || wal_frame.len() > MAX_WAL_GROUP_BYTES
    {
        return Err(MetadataError::Limit("canonical event bytes"));
    }
    let mut hasher = Sha256::new();
    hasher.update(DIGEST_DOMAIN);
    hasher.update(
        u32::try_from(metadata.len())
            .map_err(|_| MetadataError::Limit("metadata length"))?
            .to_le_bytes(),
    );
    hasher.update(metadata);
    hasher.update(
        u32::try_from(wal_frame.len())
            .map_err(|_| MetadataError::Limit("WAL length"))?
            .to_le_bytes(),
    );
    hasher.update(wal_frame);
    Ok(EventDigest::from_bytes(hasher.finalize().into()))
}

/// Validate every WAL ordinal against its metadata role, key, value, and binding.
/// Sender-local state and indexes are checked but are never merge inputs.
/// This validates bytes only; a receiver must separately certify membership and
/// schema availability before apply or any replicated acknowledgment.
///
/// # Errors
/// Rejects invalid frames, forged role mappings, digests, or document records.
pub fn verify_event(
    metadata_bytes: &[u8],
    wal_bytes: &[u8],
    expected_digest: EventDigest,
    limits: ReplicationLimits,
) -> Result<CommitEvent, MetadataError> {
    verify_event_with_sources(metadata_bytes, wal_bytes, expected_digest, limits)
        .map(|(event, _)| event)
}

#[allow(
    clippy::too_many_lines,
    reason = "all six WAL roles are matched in one ordinal pass to preserve exact correspondence"
)]
fn verify_event_with_sources<'a>(
    metadata_bytes: &[u8],
    wal_bytes: &'a [u8],
    expected_digest: EventDigest,
    limits: ReplicationLimits,
) -> Result<(CommitEvent, Vec<VerifiedSourceChange<'a>>), MetadataError> {
    let metadata = decode_metadata(metadata_bytes, limits)?;
    let frame = decode_wal_frame(wal_bytes, &FormatLimits::default())?;
    if frame.sequence() == 0
        || frame.transaction_id() != metadata.header.transaction_id
        || frame.operation_count() != metadata.roles.len()
    {
        return Err(MetadataError::Invalid("WAL transaction or operation count"));
    }
    if compute_event_digest(metadata_bytes, wal_bytes)? != expected_digest {
        return Err(MetadataError::DigestMismatch);
    }
    let id = metadata.header.id;
    let formatted_id = document_event_id(id);
    let mut source_index = BTreeMap::new();
    for (index, change) in metadata.changes.iter().enumerate() {
        source_index.insert(usize::from(change.source_wal_ordinal), index);
    }
    let mut source_values = Vec::new();
    source_values
        .try_reserve_exact(metadata.changes.len())
        .map_err(|_| MetadataError::Allocation)?;
    source_values.resize_with(metadata.changes.len(), || None);
    let mut source_exact = Vec::new();
    source_exact
        .try_reserve_exact(metadata.changes.len())
        .map_err(|_| MetadataError::Allocation)?;
    source_exact.resize_with(metadata.changes.len(), || None);
    let mut primary_seen = BTreeSet::new();
    let mut primary_values = Vec::new();
    primary_values
        .try_reserve_exact(metadata.changes.len())
        .map_err(|_| MetadataError::Allocation)?;
    for (ordinal, (role, operation)) in metadata.roles.iter().zip(frame.operations()).enumerate() {
        let operation = operation?;
        let WalOperation::Put { key, value } = operation else {
            return Err(MetadataError::Invalid("document event contains raw Delete"));
        };
        match role {
            WalRole::Marker => {
                if decode_document_key(key)?
                    != (DocumentKey::Marker {
                        origin: id.origin(),
                        sequence: id.sequence(),
                    })
                    || value != metadata_bytes
                {
                    return Err(MetadataError::Invalid("marker key or value mismatch"));
                }
            }
            WalRole::SourceChange => {
                let &index = source_index
                    .get(&ordinal)
                    .ok_or(MetadataError::Invalid("unmapped source WAL ordinal"))?;
                let change = &metadata.changes[index];
                let binding = metadata
                    .bindings
                    .binary_search_by_key(&change.collection, |binding| binding.collection)
                    .map(|index| metadata.bindings[index])
                    .map_err(|_| MetadataError::Invalid("source binding missing"))?;
                let expected_key = DocumentKey::Change {
                    collection: change.collection,
                    document: change.document,
                    origin: id.origin(),
                    sequence: id.sequence(),
                    ordinal: u16::try_from(index)
                        .map_err(|_| MetadataError::Limit("logical change ordinal"))?,
                };
                if decode_document_key(key)? != expected_key {
                    return Err(MetadataError::Invalid(
                        "source change key or ordinal mismatch",
                    ));
                }
                let decoded = decode_change(value)?;
                if decoded.event != formatted_id
                    || decoded.binding.collection != binding.collection
                    || decoded.binding.document != change.document
                    || decoded.binding.generation != binding.generation
                    || decoded.binding.schema_digest != binding.schema_digest
                    || decoded.kind != change.kind
                {
                    return Err(MetadataError::Invalid(
                        "source change value or binding mismatch",
                    ));
                }
                source_exact[index] = Some((key, value));
                source_values[index] = Some(match decoded.kind {
                    DocumentKind::Live => {
                        let mut bytes = Vec::new();
                        bytes
                            .try_reserve_exact(decoded.document_bytes.len())
                            .map_err(|_| MetadataError::Allocation)?;
                        bytes.extend_from_slice(decoded.document_bytes);
                        DocumentValue::Bytes(bytes.into_boxed_slice())
                    }
                    DocumentKind::Tombstone => DocumentValue::Tombstone,
                });
            }
            WalRole::Retry => {
                if decode_document_key(key)?
                    != (DocumentKey::Retry {
                        transaction_id: metadata.header.transaction_id,
                    })
                    || decode_retry_value(value)? != formatted_id
                {
                    return Err(MetadataError::Invalid("retry mapping mismatch"));
                }
            }
            WalRole::Primary => {
                let DocumentKey::Primary {
                    collection,
                    document,
                } = decode_document_key(key)?
                else {
                    return Err(MetadataError::Invalid("primary key role mismatch"));
                };
                if !primary_seen.insert((collection, document)) {
                    return Err(MetadataError::Invalid("duplicate primary state"));
                }
                primary_values.push((collection, document, key, value));
            }
            WalRole::LocalIndex => {
                if key.len() < 2 || !(0xE0..=0xEF).contains(&key[0]) || key[1] == 0 {
                    return Err(MetadataError::Invalid(
                        "local index key namespace or version",
                    ));
                }
            }
            WalRole::HighWater => {
                if decode_document_key(key)?
                    != (DocumentKey::HighWater {
                        origin: id.origin(),
                    })
                    || decode_high_water_value(value)? != id.sequence()
                {
                    return Err(MetadataError::Invalid("origin high-water mismatch"));
                }
            }
        }
    }
    if primary_seen.len() != metadata.changes.len() {
        return Err(MetadataError::Invalid("missing primary state"));
    }
    for (collection, document, key, value) in primary_values {
        let (index, change) = metadata
            .changes
            .iter()
            .enumerate()
            .find(|(_, change)| change.collection == collection && change.document == document)
            .ok_or(MetadataError::Invalid("primary has no logical change"))?;
        let binding = metadata
            .bindings
            .iter()
            .find(|binding| binding.collection == collection)
            .ok_or(MetadataError::Invalid("primary binding missing"))?;
        let source_value = source_values[index]
            .as_ref()
            .ok_or(MetadataError::Invalid("primary source value missing"))?;
        if source_exact[index].is_none() {
            return Err(MetadataError::Invalid("primary source D1 missing"));
        }
        match metadata.header.record_format {
            1 => {
                let state =
                    decode_state_with_limits(value, limits.max_origins, limits.max_siblings)?;
                if state.collection != collection || state.document != document {
                    return Err(MetadataError::Invalid("primary state key/value mismatch"));
                }
                let matching_value = match source_value {
                    DocumentValue::Bytes(bytes) => bytes.as_ref(),
                    DocumentValue::Tombstone => &[],
                };
                let own_context = state.context.iter().find(|dot| dot.origin == id.origin());
                if own_context.is_none_or(|dot| dot.sequence < id.sequence())
                    || change.observed.entries().any(|(origin, sequence)| {
                        state
                            .context
                            .iter()
                            .find(|dot| dot.origin == origin)
                            .is_none_or(|dot| dot.sequence < sequence)
                    })
                    || !state.siblings.iter().any(|sibling| {
                        sibling.event == formatted_id
                            && sibling.generation == binding.generation
                            && sibling.schema_digest == binding.schema_digest
                            && sibling.kind == change.kind
                            && sibling.document_bytes == matching_value
                    })
                {
                    return Err(MetadataError::Invalid(
                        "primary does not contain source dot",
                    ));
                }
            }
            2 => {
                let pointer = decode_state_pointer(key, value)?;
                if pointer.collection != collection || pointer.document != document {
                    return Err(MetadataError::Invalid("primary pointer key/value mismatch"));
                }
            }
            _ => return Err(MetadataError::Invalid("unsupported record format")),
        }
    }
    let mut changes = Vec::new();
    changes
        .try_reserve_exact(metadata.changes.len())
        .map_err(|_| MetadataError::Allocation)?;
    let mut exact_changes = Vec::new();
    exact_changes
        .try_reserve_exact(metadata.changes.len())
        .map_err(|_| MetadataError::Allocation)?;
    for (index, change) in metadata.changes.iter().enumerate() {
        let binding = *metadata
            .bindings
            .iter()
            .find(|binding| binding.collection == change.collection)
            .ok_or(MetadataError::Invalid("missing collection binding"))?;
        let (key, value) = source_exact[index]
            .take()
            .ok_or(MetadataError::Invalid("missing source change bytes"))?;
        exact_changes.push(VerifiedSourceChange {
            binding,
            document: change.document,
            kind: change.kind,
            logical_ordinal: u16::try_from(index)
                .map_err(|_| MetadataError::Limit("logical change ordinal"))?,
            source_wal_ordinal: change.source_wal_ordinal,
            key,
            value,
        });
        changes.push(DocumentChange {
            binding,
            document: change.document,
            observed: change.observed.clone(),
            value: source_values[index]
                .take()
                .ok_or(MetadataError::Invalid("missing source change"))?,
        });
    }
    let event = CommitEvent::new(
        EventHeader {
            id,
            epoch: metadata.header.epoch,
            configuration: metadata.header.configuration,
            versions: EventVersions {
                record_format: metadata.header.record_format,
                event_format: 1,
                merge_policy: metadata.header.merge_policy,
            },
            transaction_id: metadata.header.transaction_id,
            digest: expected_digest,
        },
        EncodedEventSizes {
            wal_group_bytes: wal_bytes.len(),
            distributed_metadata_bytes: metadata_bytes.len(),
        },
        changes,
        limits,
    )?;
    Ok((event, exact_changes))
}

#[cfg(test)]
mod tests {
    use tb_core::{
        Attribution, BudgetConfig, CollectionId, DatabaseId, DocumentId, MemoryBudget,
        MemoryCategory, OriginId, ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
    };
    use tb_format::{
        DocumentBinding, DocumentDot, DocumentFieldInput, DocumentSibling, FieldType, StatePointer,
        encode_change_into, encode_change_key, encode_document_into, encode_high_water_key,
        encode_high_water_value, encode_marker_key, encode_primary_key, encode_retry_key,
        encode_retry_value, encode_state_into, encode_state_pointer,
    };
    use tb_format::{FormatLimits, WalOperation, encode_wal_frame_into};

    use super::*;

    fn limits() -> ReplicationLimits {
        ReplicationLimits {
            max_origins: 8,
            max_changes: 8,
            max_siblings: 8,
            max_value_bytes: tb_core::MAX_VALUE_BYTES,
            max_payload_bytes: MAX_WAL_GROUP_BYTES,
        }
    }

    fn header() -> MetadataHeader {
        let origin = OriginId {
            replica_id: ReplicaId::from_bytes([3; 16]),
            incarnation: [4; 16],
        };
        MetadataHeader {
            id: EventId::new(DatabaseId::from_bytes([1; 16]), ShardId::new(5), origin, 1).unwrap(),
            epoch: MembershipEpoch::new(1),
            configuration: ConfigurationId::from_bytes([6; 32]).unwrap(),
            transaction_id: TransactionId::new(99, [7; 16]),
            record_format: 1,
            merge_policy: 1,
        }
    }

    type OwnedOperation = (Vec<u8>, Vec<u8>);

    fn frame(header: MetadataHeader, operations: &[OwnedOperation]) -> Vec<u8> {
        let borrowed: Vec<_> = operations
            .iter()
            .map(|(key, value)| WalOperation::Put { key, value })
            .collect();
        let mut frame = Vec::new();
        encode_wal_frame_into(
            &mut frame,
            12,
            header.transaction_id,
            &borrowed,
            &FormatLimits::default(),
        )
        .unwrap();
        frame
    }

    #[allow(
        clippy::too_many_lines,
        reason = "constructs all six roles for one exact semantic WAL fixture"
    )]
    fn fixture() -> (
        EventMetadata,
        Vec<u8>,
        Vec<OwnedOperation>,
        Vec<u8>,
        EventDigest,
    ) {
        let header = header();
        let document = DocumentId::from_bytes([9; 16]);
        let bindings = vec![
            CollectionBinding {
                collection: CollectionId::from_bytes([1; 16]),
                generation: 1,
                schema_digest: [11; 32],
            },
            CollectionBinding {
                collection: CollectionId::from_bytes([2; 16]),
                generation: 7,
                schema_digest: [12; 32],
            },
        ];
        let changes = bindings
            .iter()
            .enumerate()
            .map(|(index, binding)| MetadataChange {
                collection: binding.collection,
                document,
                source_wal_ordinal: u16::try_from(index * 2 + 1).unwrap(),
                kind: DocumentKind::Tombstone,
                observed: CausalContext::new(),
            })
            .collect::<Vec<_>>();
        let roles = vec![
            WalRole::Marker,
            WalRole::SourceChange,
            WalRole::Primary,
            WalRole::SourceChange,
            WalRole::Primary,
            WalRole::Retry,
            WalRole::HighWater,
        ];
        let mut metadata = Vec::new();
        encode_metadata_into(&mut metadata, header, &bindings, &changes, &roles, limits()).unwrap();
        let mut operations = vec![(
            encode_marker_key(header.id.origin(), 1).unwrap().to_vec(),
            metadata.clone(),
        )];
        for (index, binding) in bindings.iter().enumerate() {
            let document_binding = DocumentBinding {
                collection: binding.collection,
                document,
                generation: binding.generation,
                schema_digest: binding.schema_digest,
            };
            let mut change_value = Vec::new();
            encode_change_into(
                &mut change_value,
                document_event_id(header.id),
                document_binding,
                DocumentKind::Tombstone,
                &[],
            )
            .unwrap();
            operations.push((
                encode_change_key(
                    binding.collection,
                    document,
                    header.id.origin(),
                    1,
                    u16::try_from(index).unwrap(),
                )
                .unwrap()
                .to_vec(),
                change_value,
            ));
            let mut state = Vec::new();
            encode_state_into(
                &mut state,
                binding.collection,
                document,
                &[DocumentDot {
                    origin: header.id.origin(),
                    sequence: 1,
                }],
                &[DocumentSibling {
                    event: document_event_id(header.id),
                    generation: binding.generation,
                    schema_digest: binding.schema_digest,
                    kind: DocumentKind::Tombstone,
                    document_bytes: &[],
                }],
            )
            .unwrap();
            operations.push((
                encode_primary_key(binding.collection, document).to_vec(),
                state,
            ));
        }
        operations.push((
            encode_retry_key(header.transaction_id).to_vec(),
            encode_retry_value(document_event_id(header.id))
                .unwrap()
                .to_vec(),
        ));
        operations.push((
            encode_high_water_key(header.id.origin()).unwrap().to_vec(),
            encode_high_water_value(1).unwrap().to_vec(),
        ));
        let wal = frame(header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        (
            EventMetadata {
                header,
                bindings,
                changes,
                roles,
            },
            metadata,
            operations,
            wal,
            digest,
        )
    }

    fn fixture_v2() -> (
        EventMetadata,
        Vec<u8>,
        Vec<OwnedOperation>,
        Vec<u8>,
        EventDigest,
    ) {
        let (mut decoded, mut metadata, mut operations, _, _) = fixture();
        decoded.header.record_format = 2;
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        for (index, binding) in decoded.bindings.iter().enumerate() {
            let (key, value) = encode_state_pointer(StatePointer {
                collection: binding.collection,
                document: decoded.changes[index].document,
                manifest_sha256: [u8::try_from(index + 20).unwrap(); 32],
            });
            operations[2 * index + 2] = (key.to_vec(), value.to_vec());
        }
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        (decoded, metadata, operations, wal, digest)
    }

    #[test]
    fn format_two_pointer_proves_same_frame_d1_and_preserves_full_source_bytes() {
        let (decoded, metadata, operations, wal, digest) = fixture_v2();
        let verified = verify_event(&metadata, &wal, digest, limits()).unwrap();
        assert_eq!(verified.versions().record_format, 2);
        assert_eq!(verified.bindings(), decoded.bindings.as_slice());
        let budget = MemoryBudget::new(BudgetConfig::new(32 * 1024 * 1024, 0).unwrap());
        let proof = verify_incoming(
            &metadata,
            &wal,
            digest,
            limits(),
            &budget,
            Attribution {
                tenant: TenantId::from_bytes([41; 16]),
                worker: WorkerId(42),
                category: MemoryCategory::Replication,
            },
        )
        .unwrap();
        assert_eq!(proof.source_changes().len(), decoded.changes.len());
        let frame = decode_wal_frame(&wal, &FormatLimits::default()).unwrap();
        for (index, source) in proof.source_changes().iter().enumerate() {
            let change = &decoded.changes[index];
            assert_eq!(source.logical_ordinal(), u16::try_from(index).unwrap());
            assert_eq!(source.source_wal_ordinal(), change.source_wal_ordinal);
            assert_eq!(source.binding(), decoded.bindings[index]);
            assert_eq!(source.document(), change.document);
            assert_eq!(source.kind(), change.kind);
            assert_eq!(source.key(), operations[2 * index + 1].0.as_slice());
            assert_eq!(source.value(), operations[2 * index + 1].1.as_slice());
            let WalOperation::Put { key, value } = frame
                .operations()
                .nth(usize::from(source.source_wal_ordinal()))
                .unwrap()
                .unwrap()
            else {
                panic!("source operation is a Put")
            };
            assert_eq!(source.key().as_ptr(), key.as_ptr());
            assert_eq!(source.value().as_ptr(), value.as_ptr());
        }
        assert_eq!(budget.snapshot().reserved_items, 1);
        drop(proof);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }

    #[test]
    fn format_two_pointer_and_d1_mismatches_fail_closed() {
        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        operations[2].1[79] ^= 1;
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Format(_))
        ));

        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        operations[2].1[0] ^= 1;
        let checksum = crc32c::crc32c(&operations[2].1[..76]);
        operations[2].1[76..80].copy_from_slice(&checksum.to_le_bytes());
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Format(_))
        ));

        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        operations[2].1[8..12].copy_from_slice(&79_u32.to_le_bytes());
        let checksum = crc32c::crc32c(&operations[2].1[..76]);
        operations[2].1[76..80].copy_from_slice(&checksum.to_le_bytes());
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Format(_))
        ));

        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        let (_, other_value) = encode_state_pointer(StatePointer {
            collection: CollectionId::from_bytes([99; 16]),
            document: decoded.changes[0].document,
            manifest_sha256: [20; 32],
        });
        operations[2].1 = other_value.to_vec();
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Format(_))
        ));

        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        assert_eq!(decoded.bindings[0].generation, 1);
        operations[1].1[108..116].copy_from_slice(&2_u64.to_le_bytes());
        let value = &mut operations[1].1;
        let checksum = crc32c::crc32c(&value[..value.len() - CRC_LEN]);
        let end = value.len();
        value[end - CRC_LEN..].copy_from_slice(&checksum.to_le_bytes());
        assert_eq!(decode_change(value).unwrap().binding.generation, 2);
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid(
                "source change value or binding mismatch"
            ))
        ));

        let (mut decoded, mut metadata, mut operations, _, _) = fixture_v2();
        decoded.changes[0].kind = DocumentKind::Live;
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid(
                "source change value or binding mismatch"
            ))
        ));

        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        operations.remove(1); // A D0 pointer cannot substitute for its source D1.
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid("WAL transaction or operation count"))
        ));

        let (decoded, metadata, mut operations, _, _) = fixture_v2();
        operations[4].0[0] = 0xD6; // D6 staging is not an event primary role.
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(verify_event(&metadata, &wal, digest, limits()).is_err());
    }

    #[test]
    fn format_two_uses_logical_d1_ordinal_after_physical_wal_reordering() {
        let (mut decoded, mut metadata, mut operations, _, _) = fixture_v2();
        operations.swap(1, 3);
        decoded.changes[0].source_wal_ordinal = 3;
        decoded.changes[1].source_wal_ordinal = 1;
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        let budget = MemoryBudget::new(BudgetConfig::new(32 * 1024 * 1024, 0).unwrap());
        let proof = verify_incoming(
            &metadata,
            &wal,
            digest,
            limits(),
            &budget,
            Attribution {
                tenant: TenantId::from_bytes([41; 16]),
                worker: WorkerId(42),
                category: MemoryCategory::Replication,
            },
        )
        .unwrap();
        assert_eq!(proof.source_changes()[0].logical_ordinal(), 0);
        assert_eq!(proof.source_changes()[0].source_wal_ordinal(), 3);
        assert_eq!(proof.source_changes()[0].key(), operations[3].0.as_slice());
        assert_eq!(proof.source_changes()[1].logical_ordinal(), 1);
        assert_eq!(proof.source_changes()[1].source_wal_ordinal(), 1);
        assert_eq!(proof.source_changes()[1].key(), operations[1].0.as_slice());

        operations[3].0[75] = 3; // Physical ordinal cannot replace logical D1 ordinal.
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid(
                "source change key or ordinal mismatch"
            ))
        ));
    }

    #[test]
    fn near_limit_d1_is_preserved_as_exact_bytes_under_format_two() {
        let (mut decoded, mut metadata, mut operations, _, _) = fixture_v2();
        let first = decoded.bindings[0];
        let binding = DocumentBinding {
            collection: first.collection,
            document: decoded.changes[0].document,
            generation: first.generation,
            schema_digest: first.schema_digest,
        };
        let mut empty_document = Vec::new();
        encode_document_into(
            &mut empty_document,
            binding,
            &[DocumentFieldInput {
                field_id: 1,
                value_type: FieldType::Bytes,
                value: &[],
            }],
        )
        .unwrap();
        let body =
            vec![42; tb_core::MAX_VALUE_BYTES - operations[1].1.len() - empty_document.len()];
        let mut document = Vec::new();
        encode_document_into(
            &mut document,
            binding,
            &[DocumentFieldInput {
                field_id: 1,
                value_type: FieldType::Bytes,
                value: &body,
            }],
        )
        .unwrap();
        decoded.changes[0].kind = DocumentKind::Live;
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        encode_change_into(
            &mut operations[1].1,
            document_event_id(decoded.header.id),
            binding,
            DocumentKind::Live,
            &document,
        )
        .unwrap();
        assert_eq!(operations[1].1.len(), tb_core::MAX_VALUE_BYTES);
        let wal = frame(decoded.header, &operations);
        assert!(wal.len() <= MAX_WAL_GROUP_BYTES);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        let budget = MemoryBudget::new(BudgetConfig::new(32 * 1024 * 1024, 0).unwrap());
        let proof = verify_incoming(
            &metadata,
            &wal,
            digest,
            limits(),
            &budget,
            Attribution {
                tenant: TenantId::from_bytes([41; 16]),
                worker: WorkerId(42),
                category: MemoryCategory::Replication,
            },
        )
        .unwrap();
        assert_eq!(proof.event().versions().record_format, 2);
        assert_eq!(
            proof.source_changes()[0].value(),
            operations[1].1.as_slice()
        );
        assert_eq!(
            proof.source_changes()[0].value().len(),
            tb_core::MAX_VALUE_BYTES
        );
    }

    #[test]
    fn record_format_is_explicit_and_v1_primary_remains_v1_only() {
        let (mut decoded, mut metadata, mut operations, _, _) = fixture_v2();
        decoded.header.record_format = 1;
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Format(_))
        ));

        let (mut decoded, mut metadata, mut operations, _, _) = fixture();
        decoded.header.record_format = 2;
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Format(_))
        ));

        let (_, mut metadata, _, _, _) = fixture_v2();
        metadata[140..142].copy_from_slice(&3_u16.to_le_bytes());
        let end = metadata.len();
        let checksum = crc32c::crc32c(&metadata[..end - CRC_LEN]);
        metadata[end - CRC_LEN..].copy_from_slice(&checksum.to_le_bytes());
        assert!(matches!(
            decode_metadata(&metadata, limits()),
            Err(MetadataError::Invalid(
                "unsupported record format or merge policy"
            ))
        ));
    }

    #[test]
    fn two_collections_share_document_id_with_distinct_schema_bindings() {
        let (expected, metadata, _, wal, digest) = fixture();
        assert_eq!(decode_metadata(&metadata, limits()).unwrap(), expected);
        let event = verify_event(&metadata, &wal, digest, limits()).unwrap();
        assert_eq!(event.bindings(), expected.bindings.as_slice());
        assert_eq!(event.changes().len(), 2);
        assert_eq!(event.changes()[0].document, event.changes()[1].document);
        assert_ne!(
            event.changes()[0].binding.collection,
            event.changes()[1].binding.collection
        );
    }

    #[test]
    fn role_key_value_and_digest_mismatches_fail_closed() {
        let (expected, metadata, mut operations, wal, _) = fixture();
        assert!(matches!(
            verify_event(&metadata, &wal, EventDigest::from_bytes([0; 32]), limits()),
            Err(MetadataError::DigestMismatch)
        ));

        operations[1].0[75] = 1; // D1 key change-table ordinal, not WAL ordinal.
        let altered = frame(expected.header, &operations);
        let altered_digest = compute_event_digest(&metadata, &altered).unwrap();
        assert!(matches!(
            verify_event(&metadata, &altered, altered_digest, limits()),
            Err(MetadataError::Invalid(
                "source change key or ordinal mismatch"
            ))
        ));

        let (_, _, mut operations, _, _) = fixture();
        operations[0].1[0] ^= 1;
        let altered = frame(expected.header, &operations);
        let altered_digest = compute_event_digest(&metadata, &altered).unwrap();
        assert!(matches!(
            verify_event(&metadata, &altered, altered_digest, limits()),
            Err(MetadataError::Invalid("marker key or value mismatch"))
        ));

        let (_, _, mut operations, _, _) = fixture();
        operations[4].0[0] = 0xD5;
        let altered = frame(expected.header, &operations);
        let altered_digest = compute_event_digest(&metadata, &altered).unwrap();
        assert!(verify_event(&metadata, &altered, altered_digest, limits()).is_err());
    }

    #[test]
    fn duplicate_change_bad_role_and_unclassified_extra_put_are_rejected() {
        let (mut decoded, _, mut operations, _, _) = fixture();
        decoded.changes[1].collection = decoded.changes[0].collection;
        decoded.changes[1].document = decoded.changes[0].document;
        assert!(
            encode_metadata_into(
                &mut Vec::new(),
                decoded.header,
                &decoded.bindings,
                &decoded.changes,
                &decoded.roles,
                limits()
            )
            .is_err()
        );

        let (mut decoded, _, _, _, _) = fixture();
        decoded.roles[1] = WalRole::LocalIndex;
        assert!(
            encode_metadata_into(
                &mut Vec::new(),
                decoded.header,
                &decoded.bindings,
                &decoded.changes,
                &decoded.roles,
                limits()
            )
            .is_err()
        );

        let (mut decoded, _, _, _, _) = fixture();
        decoded.roles.push(WalRole::LocalIndex);
        decoded.changes[0].source_wal_ordinal = 1;
        let mut metadata = Vec::new();
        encode_metadata_into(
            &mut metadata,
            decoded.header,
            &decoded.bindings,
            &decoded.changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        operations[0].1 = metadata.clone();
        operations.push((vec![0xD5, 1, 8], vec![9]));
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid(
                "local index key namespace or version"
            ))
        ));
    }

    #[test]
    fn raw_delete_forged_binding_and_bad_causal_order_are_rejected() {
        let (decoded, metadata, operations, _, _) = fixture();
        let borrowed: Vec<_> = operations
            .iter()
            .enumerate()
            .map(|(index, (key, value))| {
                if index == 1 {
                    WalOperation::Delete {
                        key: key.as_slice(),
                    }
                } else {
                    WalOperation::Put {
                        key: key.as_slice(),
                        value: value.as_slice(),
                    }
                }
            })
            .collect();
        let mut wal = Vec::new();
        encode_wal_frame_into(
            &mut wal,
            12,
            decoded.header.transaction_id,
            &borrowed,
            &FormatLimits::default(),
        )
        .unwrap();
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid("document event contains raw Delete"))
        ));

        let (_, _, mut operations, _, _) = fixture();
        let first = decoded.bindings[0];
        encode_change_into(
            &mut operations[1].1,
            document_event_id(decoded.header.id),
            DocumentBinding {
                collection: first.collection,
                document: decoded.changes[0].document,
                generation: first.generation,
                schema_digest: [99; 32],
            },
            DocumentKind::Tombstone,
            &[],
        )
        .unwrap();
        let wal = frame(decoded.header, &operations);
        let digest = compute_event_digest(&metadata, &wal).unwrap();
        assert!(matches!(
            verify_event(&metadata, &wal, digest, limits()),
            Err(MetadataError::Invalid(
                "source change value or binding mismatch"
            ))
        ));

        let mut changes = decoded.changes.clone();
        let other_a = OriginId {
            replica_id: ReplicaId::from_bytes([30; 16]),
            incarnation: [30; 16],
        };
        let other_b = OriginId {
            replica_id: ReplicaId::from_bytes([31; 16]),
            incarnation: [31; 16],
        };
        changes[0].observed =
            CausalContext::from_entries([(other_a, 1), (other_b, 1)], limits().max_origins)
                .unwrap();
        let mut bytes = Vec::new();
        encode_metadata_into(
            &mut bytes,
            decoded.header,
            &decoded.bindings,
            &changes,
            &decoded.roles,
            limits(),
        )
        .unwrap();
        let causal_start = HEADER_LEN + decoded.bindings.len() * BINDING_LEN + CHANGE_LEN;
        let first_entry = bytes[causal_start..causal_start + CAUSAL_LEN].to_vec();
        let second_entry = bytes[causal_start + CAUSAL_LEN..causal_start + 2 * CAUSAL_LEN].to_vec();
        bytes[causal_start..causal_start + CAUSAL_LEN].copy_from_slice(&second_entry);
        bytes[causal_start + CAUSAL_LEN..causal_start + 2 * CAUSAL_LEN]
            .copy_from_slice(&first_entry);
        let checksum = crc32c::crc32c(&bytes[..bytes.len() - CRC_LEN]);
        let end = bytes.len();
        bytes[end - CRC_LEN..].copy_from_slice(&checksum.to_le_bytes());
        assert!(matches!(
            decode_metadata(&bytes, limits()),
            Err(MetadataError::Invalid(
                "causal entries are not strictly sorted"
            ))
        ));
    }

    #[test]
    fn unknown_role_is_rejected_even_with_valid_checksum() {
        let (_, mut bytes, _, _, _) = fixture();
        let role_offset = bytes.len() - CRC_LEN - 1;
        bytes[role_offset] = 7;
        let checksum = crc32c::crc32c(&bytes[..bytes.len() - CRC_LEN]);
        let end = bytes.len();
        bytes[end - CRC_LEN..].copy_from_slice(&checksum.to_le_bytes());
        assert!(matches!(
            decode_metadata(&bytes, limits()),
            Err(MetadataError::Invalid("unknown WAL role"))
        ));
    }

    #[test]
    fn truncated_sections_fail_before_count_driven_allocation() {
        let (_, mut bytes, _, _, _) = fixture();
        let binding_count_offset = HEADER_LEN - 3 * std::mem::size_of::<u16>();
        assert_eq!(
            u16::from_le_bytes(
                bytes[binding_count_offset..binding_count_offset + 2]
                    .try_into()
                    .unwrap()
            ),
            2
        );
        bytes[binding_count_offset..binding_count_offset + 2]
            .copy_from_slice(&1_024_u16.to_le_bytes());
        let checksum = crc32c::crc32c(&bytes[..bytes.len() - CRC_LEN]);
        let end = bytes.len();
        bytes[end - CRC_LEN..].copy_from_slice(&checksum.to_le_bytes());
        assert!(matches!(
            decode_metadata(&bytes, limits()),
            Err(MetadataError::Invalid("truncated metadata sections"))
        ));

        let (_, mut bytes, _, _, _) = fixture();
        let causal_count_offset = HEADER_LEN + 2 * BINDING_LEN + 21;
        bytes[causal_count_offset..causal_count_offset + 2].copy_from_slice(&8_u16.to_le_bytes());
        let checksum = crc32c::crc32c(&bytes[..bytes.len() - CRC_LEN]);
        let end = bytes.len();
        bytes[end - CRC_LEN..].copy_from_slice(&checksum.to_le_bytes());
        assert!(matches!(
            decode_metadata(&bytes, limits()),
            Err(MetadataError::Invalid("truncated causal section"))
        ));
    }

    #[test]
    fn canonical_digest_framing_matches_journal() {
        let expected = EventDigest::from_bytes([
            0x63, 0xe9, 0xc6, 0xc2, 0xae, 0x27, 0x7f, 0xe5, 0x00, 0x50, 0x3c, 0x70, 0x89, 0xe4,
            0x4a, 0x07, 0x40, 0x74, 0x89, 0xd3, 0xf8, 0xe1, 0xae, 0x26, 0x71, 0xcc, 0x3b, 0xc6,
            0x86, 0x8a, 0x90, 0x1a,
        ]);
        assert_eq!(compute_event_digest(b"a", b"b").unwrap(), expected);
        assert_eq!(crate::journal::digest_event(b"a", b"b").unwrap(), expected);
        assert_ne!(compute_event_digest(b"ab", b"c").unwrap(), expected);
    }

    #[test]
    fn incoming_proof_holds_semantic_byte_and_item_admission() {
        let (_, metadata, _, wal, digest) = fixture();
        let attribution = Attribution {
            tenant: TenantId::from_bytes([41; 16]),
            worker: WorkerId(42),
            category: MemoryCategory::Other,
        };
        let required = semantic_reservation_bytes(metadata.len(), wal.len()).unwrap();
        let small_budget = MemoryBudget::new(BudgetConfig::new(required - 1, 0).unwrap());
        assert!(matches!(
            verify_incoming(
                &metadata,
                &wal,
                digest,
                limits(),
                &small_budget,
                attribution
            ),
            Err(MetadataError::Admission(_))
        ));
        assert_eq!(small_budget.snapshot().reserved_bytes, 0);
        assert_eq!(small_budget.snapshot().reserved_items, 0);

        let item_budget = MemoryBudget::new(
            BudgetConfig::new(required * 2, 0)
                .unwrap()
                .with_item_limits(0, 0, 0, 0),
        );
        assert!(matches!(
            verify_incoming(&metadata, &wal, digest, limits(), &item_budget, attribution),
            Err(MetadataError::Admission(_))
        ));
        assert_eq!(item_budget.snapshot().reserved_bytes, 0);

        let budget = MemoryBudget::new(BudgetConfig::new(required * 2, 0).unwrap());
        let proof =
            verify_incoming(&metadata, &wal, digest, limits(), &budget, attribution).unwrap();
        assert_eq!(proof.event().id(), header().id);
        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_bytes, required);
        assert_eq!(snapshot.reserved_items, 1);
        assert_eq!(
            snapshot.categories[MemoryCategory::Replication as usize],
            required
        );
        drop(proof);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }
}
