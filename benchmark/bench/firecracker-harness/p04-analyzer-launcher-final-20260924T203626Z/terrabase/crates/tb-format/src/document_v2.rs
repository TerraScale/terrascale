//! Bounded physical codecs for referenced document state and applied identity.
//!
//! D0 and D6 are local projections. Their references never replace the D1
//! source change as replication authority.

#![allow(
    clippy::missing_panics_doc,
    reason = "fixed-width conversions follow exact length and checksum validation"
)]

use sha2::{Digest, Sha256};
use tb_core::{
    CollectionId, DatabaseId, DocumentId, MembershipEpoch, OriginId, ReplicaId, ShardId,
};

use crate::FormatError;
use crate::document::{
    DocumentChangeView, DocumentDot, DocumentEventId, DocumentKey, DocumentKind, decode_change,
    decode_document_key, encode_change_key, encode_primary_key,
};

const FORMAT: &str = "document v2 state";
const VERSION: u8 = 1;
const MANIFEST_MAGIC: &[u8; 8] = b"TBMAN001";
const POINTER_MAGIC: &[u8; 8] = b"TBPTR002";
const APPLIED_MAGIC: &[u8; 8] = b"TBAPL001";
const MANIFEST_HEADER_LEN: usize = 48;
const DOT_LEN: usize = 40;
const DESCRIPTOR_LEN: usize = 143;
const EVENT_LEN: usize = 64;
const CRC_LEN: usize = 4;
const HARD_COUNT: usize = 1_024;

/// Exact encoded D0 v2 pointer value length.
pub const STATE_POINTER_VALUE_LEN: usize = 80;
/// Exact encoded D6 manifest key length.
pub const CAUSAL_MANIFEST_KEY_LEN: usize = 66;
/// Maximum encoded D6 manifest value length at the hard count caps.
pub const CAUSAL_MANIFEST_MAX_VALUE_LEN: usize = 187_444;
/// Exact encoded D5 applied-identity key length.
pub const APPLIED_KEY_LEN: usize = 42;
/// Exact encoded D5 applied-identity value length.
pub const APPLIED_VALUE_LEN: usize = 156;

/// A D0 v2 pointer to one immutable, content-addressed D6 manifest.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct StatePointer {
    pub collection: CollectionId,
    pub document: DocumentId,
    pub manifest_sha256: [u8; 32],
}

/// An immutable D1 reference and the checks needed when following it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SiblingDescriptor {
    pub event: DocumentEventId,
    /// Ordinal in the logical change table, not the source WAL ordinal.
    pub ordinal: u16,
    pub generation: u64,
    pub schema_digest: [u8; 32],
    pub kind: DocumentKind,
    pub change_value_len: u32,
    pub change_sha256: [u8; 32],
}

/// A fully validated D6 manifest. Callers reserve its input/output memory.
#[derive(Clone, Debug)]
pub struct CausalManifest {
    collection: CollectionId,
    document: DocumentId,
    context: Vec<DocumentDot>,
    siblings: Vec<SiblingDescriptor>,
    sha256: [u8; 32],
}

impl CausalManifest {
    #[must_use]
    pub const fn collection(&self) -> CollectionId {
        self.collection
    }

    #[must_use]
    pub const fn document(&self) -> DocumentId {
        self.document
    }

    #[must_use]
    pub fn context(&self) -> &[DocumentDot] {
        &self.context
    }

    #[must_use]
    pub fn siblings(&self) -> &[SiblingDescriptor] {
        &self.siblings
    }

    #[must_use]
    pub const fn sha256(&self) -> [u8; 32] {
        self.sha256
    }
}

/// Durable receiver replay identity, independent of sender retry tokens.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AppliedIdentity {
    pub event: DocumentEventId,
    pub digest: [u8; 32],
    pub epoch: MembershipEpoch,
    pub configuration: [u8; 32],
}

fn invalid(reason: &'static str) -> FormatError {
    FormatError::Invalid {
        format: FORMAT,
        reason,
    }
}

fn check_fixed<const N: usize>(value: &[u8], magic: [u8; 8]) -> Result<(), FormatError> {
    if value.len() < N {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: N,
            actual: value.len(),
        });
    }
    if value.len() != N
        || value[..8] != magic
        || u32::from_le_bytes(value[8..12].try_into().unwrap())
            != u32::try_from(N).map_err(|_| invalid("fixed length exceeds u32"))?
    {
        return Err(invalid("fixed magic or exact length"));
    }
    check_crc(value)
}

fn check_crc(value: &[u8]) -> Result<(), FormatError> {
    let split = value.len() - CRC_LEN;
    if crc32c::crc32c(&value[..split]) != u32::from_le_bytes(value[split..].try_into().unwrap()) {
        return Err(FormatError::ChecksumMismatch { format: FORMAT });
    }
    Ok(())
}

fn finish_crc(value: &mut Vec<u8>) {
    value.extend_from_slice(&crc32c::crc32c(value).to_le_bytes());
}

fn event_bytes(event: DocumentEventId) -> [u8; EVENT_LEN] {
    let mut bytes = [0; EVENT_LEN];
    bytes[..16].copy_from_slice(&event.database.to_bytes());
    bytes[16..24].copy_from_slice(&event.shard.get().to_le_bytes());
    bytes[24..40].copy_from_slice(&event.origin.replica_id.to_bytes());
    bytes[40..56].copy_from_slice(&event.origin.incarnation);
    bytes[56..64].copy_from_slice(&event.sequence.to_le_bytes());
    bytes
}

fn parse_event(bytes: &[u8]) -> Result<DocumentEventId, FormatError> {
    DocumentEventId {
        database: DatabaseId::from_bytes(bytes[..16].try_into().unwrap()),
        shard: ShardId::new(u64::from_le_bytes(bytes[16..24].try_into().unwrap())),
        origin: OriginId {
            replica_id: ReplicaId::from_bytes(bytes[24..40].try_into().unwrap()),
            incarnation: bytes[40..56].try_into().unwrap(),
        },
        sequence: u64::from_le_bytes(bytes[56..64].try_into().unwrap()),
    }
    .validate()
}

/// Encode the existing D0 key and its 80-byte v2 pointer value.
#[must_use]
pub fn encode_state_pointer(pointer: StatePointer) -> ([u8; 34], [u8; STATE_POINTER_VALUE_LEN]) {
    let key = encode_primary_key(pointer.collection, pointer.document);
    let mut value = [0; STATE_POINTER_VALUE_LEN];
    value[..8].copy_from_slice(POINTER_MAGIC);
    value[8..12].copy_from_slice(&80_u32.to_le_bytes());
    value[12..28].copy_from_slice(&pointer.collection.to_bytes());
    value[28..44].copy_from_slice(&pointer.document.to_bytes());
    value[44..76].copy_from_slice(&pointer.manifest_sha256);
    let checksum = crc32c::crc32c(&value[..76]);
    value[76..80].copy_from_slice(&checksum.to_le_bytes());
    (key, value)
}

/// Decode only a D0 v2 pointer; the legacy `TBSTA001` decoder is unchanged.
///
/// # Errors
/// Rejects a mismatched key, version, length or checksum.
pub fn decode_state_pointer(key: &[u8], value: &[u8]) -> Result<StatePointer, FormatError> {
    let DocumentKey::Primary {
        collection,
        document,
    } = decode_document_key(key)?
    else {
        return Err(invalid("not a D0 primary key"));
    };
    check_fixed::<STATE_POINTER_VALUE_LEN>(value, *POINTER_MAGIC)?;
    if value[12..28] != collection.to_bytes() || value[28..44] != document.to_bytes() {
        return Err(invalid("D0 pointer key and value IDs differ"));
    }
    Ok(StatePointer {
        collection,
        document,
        manifest_sha256: value[44..76].try_into().unwrap(),
    })
}

/// Encode a D6 key from its complete value hash.
#[must_use]
pub fn encode_causal_manifest_key(
    collection: CollectionId,
    document: DocumentId,
    sha256: [u8; 32],
) -> [u8; CAUSAL_MANIFEST_KEY_LEN] {
    let mut key = [0; CAUSAL_MANIFEST_KEY_LEN];
    key[..2].copy_from_slice(&[0xD6, VERSION]);
    key[2..18].copy_from_slice(&collection.to_bytes());
    key[18..34].copy_from_slice(&document.to_bytes());
    key[34..66].copy_from_slice(&sha256);
    key
}

fn manifest_key_parts(key: &[u8]) -> Result<(CollectionId, DocumentId, [u8; 32]), FormatError> {
    if key.len() != CAUSAL_MANIFEST_KEY_LEN || key[..2] != [0xD6, VERSION] {
        return Err(invalid("D6 key tag, version or length"));
    }
    Ok((
        CollectionId::from_bytes(key[2..18].try_into().unwrap()),
        DocumentId::from_bytes(key[18..34].try_into().unwrap()),
        key[34..66].try_into().unwrap(),
    ))
}

fn validate_manifest(
    database: DatabaseId,
    shard: ShardId,
    context: &[DocumentDot],
    siblings: &[SiblingDescriptor],
    max_origins: usize,
    max_siblings: usize,
) -> Result<(), FormatError> {
    if context.len() > max_origins.min(HARD_COUNT)
        || siblings.is_empty()
        || siblings.len() > max_siblings.min(HARD_COUNT)
    {
        return Err(invalid("D6 causal or sibling count limit"));
    }
    for pair in context.windows(2) {
        if pair[0].origin >= pair[1].origin {
            return Err(invalid("D6 context order or duplicate"));
        }
    }
    for dot in context {
        if dot.origin.incarnation == [0; 16] || dot.sequence == 0 {
            return Err(invalid("D6 invalid context dot"));
        }
    }
    for pair in siblings.windows(2) {
        if pair[0].event >= pair[1].event {
            return Err(invalid("D6 sibling order or duplicate"));
        }
    }
    for sibling in siblings {
        sibling.event.validate()?;
        if sibling.event.database != database
            || sibling.event.shard != shard
            || sibling.generation == 0
            || !(157..=1_048_576).contains(&sibling.change_value_len)
        {
            return Err(invalid("D6 sibling scope, generation or D1 length"));
        }
        if context
            .binary_search_by_key(&sibling.event.origin, |dot| dot.origin)
            .ok()
            .is_none_or(|index| context[index].sequence < sibling.event.sequence)
        {
            return Err(invalid("D6 sibling dot is not covered"));
        }
    }
    Ok(())
}

/// Encode one canonical D6 manifest, returning SHA-256 of its entire value.
///
/// The output is replaced only after all structural checks and allocation pass.
/// # Errors
/// Rejects invalid order, scope, counts or allocation failure.
#[allow(clippy::too_many_arguments)]
pub fn encode_causal_manifest_into(
    output: &mut Vec<u8>,
    collection: CollectionId,
    document: DocumentId,
    database: DatabaseId,
    shard: ShardId,
    context: &[DocumentDot],
    siblings: &[SiblingDescriptor],
    max_origins: usize,
    max_siblings: usize,
) -> Result<[u8; 32], FormatError> {
    validate_manifest(
        database,
        shard,
        context,
        siblings,
        max_origins,
        max_siblings,
    )?;
    let len = 52 + DOT_LEN * context.len() + DESCRIPTOR_LEN * siblings.len();
    let mut value = Vec::new();
    value
        .try_reserve_exact(len)
        .map_err(|_| FormatError::Allocation)?;
    value.extend_from_slice(MANIFEST_MAGIC);
    let encoded_len = u32::try_from(len).map_err(|_| invalid("D6 length exceeds u32"))?;
    value.extend_from_slice(&encoded_len.to_le_bytes());
    value.extend_from_slice(&collection.to_bytes());
    value.extend_from_slice(&document.to_bytes());
    let context_count =
        u16::try_from(context.len()).map_err(|_| invalid("D6 context count exceeds u16"))?;
    let sibling_count =
        u16::try_from(siblings.len()).map_err(|_| invalid("D6 sibling count exceeds u16"))?;
    value.extend_from_slice(&context_count.to_le_bytes());
    value.extend_from_slice(&sibling_count.to_le_bytes());
    for dot in context {
        value.extend_from_slice(&dot.origin.replica_id.to_bytes());
        value.extend_from_slice(&dot.origin.incarnation);
        value.extend_from_slice(&dot.sequence.to_le_bytes());
    }
    for sibling in siblings {
        value.extend_from_slice(&event_bytes(sibling.event));
        value.extend_from_slice(&sibling.ordinal.to_le_bytes());
        value.extend_from_slice(&sibling.generation.to_le_bytes());
        value.extend_from_slice(&sibling.schema_digest);
        value.push(sibling.kind as u8);
        value.extend_from_slice(&sibling.change_value_len.to_le_bytes());
        value.extend_from_slice(&sibling.change_sha256);
    }
    finish_crc(&mut value);
    debug_assert_eq!(value.len(), len);
    let sha256 = Sha256::digest(&value).into();
    *output = value;
    Ok(sha256)
}

/// Decode and hash-bind a D6 key/value under one expected database/shard.
///
/// # Errors
/// Rejects a malformed key/value, CRC/hash mismatch, or invalid references.
pub fn decode_causal_manifest(
    key: &[u8],
    value: &[u8],
    database: DatabaseId,
    shard: ShardId,
    max_origins: usize,
    max_siblings: usize,
) -> Result<CausalManifest, FormatError> {
    let (collection, document, expected_sha) = manifest_key_parts(key)?;
    if value.len() < 52 || value.len() > CAUSAL_MANIFEST_MAX_VALUE_LEN {
        return Err(invalid("D6 value length limit"));
    }
    if &value[..8] != MANIFEST_MAGIC
        || u32::from_le_bytes(value[8..12].try_into().unwrap()) as usize != value.len()
    {
        return Err(invalid("D6 magic or exact declared length"));
    }
    check_crc(value)?;
    if value[12..28] != collection.to_bytes() || value[28..44] != document.to_bytes() {
        return Err(invalid("D6 key and value IDs differ"));
    }
    let count_context = u16::from_le_bytes(value[44..46].try_into().unwrap()) as usize;
    let count_siblings = u16::from_le_bytes(value[46..48].try_into().unwrap()) as usize;
    if count_context > max_origins.min(HARD_COUNT)
        || count_siblings == 0
        || count_siblings > max_siblings.min(HARD_COUNT)
        || 52 + DOT_LEN * count_context + DESCRIPTOR_LEN * count_siblings != value.len()
    {
        return Err(invalid("D6 count or exact length"));
    }
    let actual_sha: [u8; 32] = Sha256::digest(value).into();
    if actual_sha != expected_sha {
        return Err(invalid("D6 content-address hash mismatch"));
    }
    let mut context = Vec::new();
    context
        .try_reserve_exact(count_context)
        .map_err(|_| FormatError::Allocation)?;
    let mut siblings = Vec::new();
    siblings
        .try_reserve_exact(count_siblings)
        .map_err(|_| FormatError::Allocation)?;
    let mut at = MANIFEST_HEADER_LEN;
    for _ in 0..count_context {
        context.push(DocumentDot {
            origin: OriginId {
                replica_id: ReplicaId::from_bytes(value[at..at + 16].try_into().unwrap()),
                incarnation: value[at + 16..at + 32].try_into().unwrap(),
            },
            sequence: u64::from_le_bytes(value[at + 32..at + 40].try_into().unwrap()),
        });
        at += DOT_LEN;
    }
    for _ in 0..count_siblings {
        siblings.push(SiblingDescriptor {
            event: parse_event(&value[at..at + 64])?,
            ordinal: u16::from_le_bytes(value[at + 64..at + 66].try_into().unwrap()),
            generation: u64::from_le_bytes(value[at + 66..at + 74].try_into().unwrap()),
            schema_digest: value[at + 74..at + 106].try_into().unwrap(),
            kind: DocumentKind::try_from(value[at + 106])?,
            change_value_len: u32::from_le_bytes(value[at + 107..at + 111].try_into().unwrap()),
            change_sha256: value[at + 111..at + 143].try_into().unwrap(),
        });
        at += DESCRIPTOR_LEN;
    }
    validate_manifest(
        database,
        shard,
        &context,
        &siblings,
        max_origins,
        max_siblings,
    )?;
    Ok(CausalManifest {
        collection,
        document,
        context,
        siblings,
        sha256: actual_sha,
    })
}

/// Bind a validated D0 pointer to the exact D6 manifest it names.
/// # Errors
/// Rejects a different collection, document, or complete-value hash.
pub fn verify_state_pointer_manifest(
    pointer: StatePointer,
    manifest: &CausalManifest,
) -> Result<(), FormatError> {
    if pointer.collection != manifest.collection
        || pointer.document != manifest.document
        || pointer.manifest_sha256 != manifest.sha256
    {
        return Err(invalid("D0 pointer and D6 manifest differ"));
    }
    Ok(())
}

/// Derive the authoritative D1 key named by a D6 descriptor.
/// # Errors
/// Rejects a zero event sequence or origin incarnation.
pub fn descriptor_change_key(
    manifest: &CausalManifest,
    descriptor: SiblingDescriptor,
) -> Result<[u8; 76], FormatError> {
    encode_change_key(
        manifest.collection,
        manifest.document,
        descriptor.event.origin,
        descriptor.event.sequence,
        descriptor.ordinal,
    )
}

/// Build one descriptor from an already encoded, canonical D1 key/value.
///
/// The key ordinal is the logical change-table ordinal. The source WAL
/// ordinal does not enter the descriptor.
/// # Errors
/// Rejects mismatched key/value identity, malformed D1 or length overflow.
pub fn describe_change(key: &[u8], value: &[u8]) -> Result<SiblingDescriptor, FormatError> {
    let DocumentKey::Change {
        collection,
        document,
        origin,
        sequence,
        ordinal,
    } = decode_document_key(key)?
    else {
        return Err(invalid("not a D1 change key"));
    };
    let change = decode_change(value)?;
    if change.event.origin != origin
        || change.event.sequence != sequence
        || change.binding.collection != collection
        || change.binding.document != document
    {
        return Err(invalid("D1 key and value identity mismatch"));
    }
    Ok(SiblingDescriptor {
        event: change.event,
        ordinal,
        generation: change.binding.generation,
        schema_digest: change.binding.schema_digest,
        kind: change.kind,
        change_value_len: u32::try_from(value.len())
            .map_err(|_| invalid("D1 length exceeds u32"))?,
        change_sha256: Sha256::digest(value).into(),
    })
}

/// Verify the exact immutable D1 key/value followed from a D6 descriptor.
/// # Errors
/// Rejects a changed payload, wrong binding, kind, identity or derived key.
pub fn verify_descriptor_change<'a>(
    manifest: &CausalManifest,
    descriptor: SiblingDescriptor,
    key: &[u8],
    value: &'a [u8],
) -> Result<DocumentChangeView<'a>, FormatError> {
    if !manifest.siblings.contains(&descriptor)
        || key != descriptor_change_key(manifest, descriptor)?
    {
        return Err(invalid("D6 descriptor D1 key mismatch"));
    }
    let actual_sha: [u8; 32] = Sha256::digest(value).into();
    if value.len() != descriptor.change_value_len as usize || actual_sha != descriptor.change_sha256
    {
        return Err(invalid("D6 descriptor D1 length or hash mismatch"));
    }
    let change = decode_change(value)?;
    if change.event != descriptor.event
        || change.binding.collection != manifest.collection
        || change.binding.document != manifest.document
        || change.binding.generation != descriptor.generation
        || change.binding.schema_digest != descriptor.schema_digest
        || change.kind != descriptor.kind
    {
        return Err(invalid("D6 descriptor D1 value mismatch"));
    }
    Ok(change)
}

/// Encode a D5 applied-identity key with big-endian sequence ordering.
/// # Errors
/// Rejects an invalid event origin or sequence.
pub fn encode_applied_key(event: DocumentEventId) -> Result<[u8; APPLIED_KEY_LEN], FormatError> {
    event.validate()?;
    let mut key = [0; APPLIED_KEY_LEN];
    key[..2].copy_from_slice(&[0xD5, VERSION]);
    key[2..18].copy_from_slice(&event.origin.replica_id.to_bytes());
    key[18..34].copy_from_slice(&event.origin.incarnation);
    key[34..42].copy_from_slice(&event.sequence.to_be_bytes());
    Ok(key)
}

/// Encode the fixed 156-byte receiver applied identity.
/// # Errors
/// Rejects zero epoch/configuration or invalid event identity.
pub fn encode_applied_value(
    identity: AppliedIdentity,
) -> Result<[u8; APPLIED_VALUE_LEN], FormatError> {
    identity.event.validate()?;
    if identity.epoch.get() == 0 || identity.configuration == [0; 32] {
        return Err(invalid("D5 uncertified epoch or configuration"));
    }
    let mut value = [0; APPLIED_VALUE_LEN];
    value[..8].copy_from_slice(APPLIED_MAGIC);
    value[8..12].copy_from_slice(&156_u32.to_le_bytes());
    value[16..80].copy_from_slice(&event_bytes(identity.event));
    value[80..112].copy_from_slice(&identity.digest);
    value[112..120].copy_from_slice(&identity.epoch.get().to_le_bytes());
    value[120..152].copy_from_slice(&identity.configuration);
    let checksum = crc32c::crc32c(&value[..152]);
    value[152..156].copy_from_slice(&checksum.to_le_bytes());
    Ok(value)
}

/// Decode D5 only for the opened certified database/shard scope.
/// # Errors
/// Rejects key/value disagreement, flags, checksum, scope or identity failure.
pub fn decode_applied_identity(
    key: &[u8],
    value: &[u8],
    database: DatabaseId,
    shard: ShardId,
) -> Result<AppliedIdentity, FormatError> {
    if key.len() != APPLIED_KEY_LEN || key[..2] != [0xD5, VERSION] {
        return Err(invalid("D5 key tag, version or length"));
    }
    check_fixed::<APPLIED_VALUE_LEN>(value, *APPLIED_MAGIC)?;
    if value[12..16] != [0; 4] {
        return Err(invalid("D5 reserved flags"));
    }
    let event = parse_event(&value[16..80])?;
    if event.database != database
        || event.shard != shard
        || key[2..18] != event.origin.replica_id.to_bytes()
        || key[18..34] != event.origin.incarnation
        || key[34..42] != event.sequence.to_be_bytes()
    {
        return Err(invalid("D5 key, value or certified scope mismatch"));
    }
    let epoch = MembershipEpoch::new(u64::from_le_bytes(value[112..120].try_into().unwrap()));
    let configuration = value[120..152].try_into().unwrap();
    if epoch.get() == 0 || configuration == [0; 32] {
        return Err(invalid("D5 uncertified epoch or configuration"));
    }
    Ok(AppliedIdentity {
        event,
        digest: value[80..112].try_into().unwrap(),
        epoch,
        configuration,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        DocumentBinding, DocumentFieldInput, DocumentSibling, FieldType, FormatLimits,
        WalOperation, decode_change, decode_state, decode_wal_frame, encode_change_into,
        encode_document_into, encode_state_into, encode_wal_frame_into,
    };
    use tb_core::TransactionId;

    fn scope() -> (DatabaseId, ShardId, CollectionId, DocumentId) {
        (
            DatabaseId::from_bytes([1; 16]),
            ShardId::new(7),
            CollectionId::from_bytes([2; 16]),
            DocumentId::from_bytes([3; 16]),
        )
    }

    fn origin(number: u16) -> OriginId {
        let mut replica = [0; 16];
        replica[14..].copy_from_slice(&number.to_be_bytes());
        OriginId {
            replica_id: ReplicaId::from_bytes(replica),
            incarnation: [4; 16],
        }
    }

    fn descriptor(number: u16, database: DatabaseId, shard: ShardId) -> SiblingDescriptor {
        SiblingDescriptor {
            event: DocumentEventId {
                database,
                shard,
                origin: origin(number),
                sequence: 1,
            },
            ordinal: number,
            generation: 3,
            schema_digest: [5; 32],
            kind: DocumentKind::Tombstone,
            change_value_len: 157,
            change_sha256: [6; 32],
        }
    }

    fn manifest_for(document: DocumentId, count: u16) -> (Vec<u8>, [u8; 66]) {
        let (database, shard, collection, _) = scope();
        let context = (1..=count)
            .map(|number| DocumentDot {
                origin: origin(number),
                sequence: 1,
            })
            .collect::<Vec<_>>();
        let siblings = (1..=count)
            .map(|number| descriptor(number, database, shard))
            .collect::<Vec<_>>();
        let mut value = Vec::new();
        let hash = encode_causal_manifest_into(
            &mut value, collection, document, database, shard, &context, &siblings, HARD_COUNT,
            HARD_COUNT,
        )
        .unwrap();
        (
            value,
            encode_causal_manifest_key(collection, document, hash),
        )
    }

    fn frame(operations: &[(Vec<u8>, Vec<u8>)]) -> Result<Vec<u8>, FormatError> {
        let borrowed = operations
            .iter()
            .map(|(key, value)| WalOperation::Put {
                key: key.as_slice(),
                value: value.as_slice(),
            })
            .collect::<Vec<_>>();
        let mut bytes = Vec::new();
        encode_wal_frame_into(
            &mut bytes,
            1,
            TransactionId::new(u64::MAX, [8; 16]),
            &borrowed,
            &FormatLimits::default(),
        )?;
        Ok(bytes)
    }

    fn sized_live_change(
        binding: DocumentBinding,
        event: DocumentEventId,
        target_len: usize,
    ) -> Vec<u8> {
        // A one-Bytes-field TBDOC001 adds 111 bytes to its payload; TBCHG001
        // adds 157 more. These are syntax-valid D1 bytes, not a schema claim.
        let payload = vec![7; target_len - 268];
        let mut document = Vec::new();
        encode_document_into(
            &mut document,
            binding,
            &[DocumentFieldInput {
                field_id: 1,
                value_type: FieldType::Bytes,
                value: &payload,
            }],
        )
        .unwrap();
        let mut change = Vec::new();
        encode_change_into(&mut change, event, binding, DocumentKind::Live, &document).unwrap();
        assert_eq!(change.len(), target_len);
        assert!(decode_change(&change).is_ok());
        change
    }

    #[test]
    fn pointer_manifest_and_applied_identity_have_exact_offsets_and_bindings() {
        let (database, shard, collection, document) = scope();
        let event = descriptor(1, database, shard).event;
        let binding = DocumentBinding {
            collection,
            document,
            generation: 3,
            schema_digest: [5; 32],
        };
        let mut change = Vec::new();
        encode_change_into(&mut change, event, binding, DocumentKind::Tombstone, &[]).unwrap();
        assert_eq!(change.len(), 157);
        let desc = SiblingDescriptor {
            change_sha256: Sha256::digest(&change).into(),
            ..descriptor(1, database, shard)
        };
        let context = [DocumentDot {
            origin: event.origin,
            sequence: 1,
        }];
        let mut manifest_value = Vec::new();
        let hash = encode_causal_manifest_into(
            &mut manifest_value,
            collection,
            document,
            database,
            shard,
            &context,
            &[desc],
            1,
            1,
        )
        .unwrap();
        assert_eq!(manifest_value.len(), 235);
        assert_eq!(manifest_value[48..64], event.origin.replica_id.to_bytes());
        assert_eq!(&manifest_value[88..152], &event_bytes(event));
        assert_eq!(&manifest_value[152..154], &1_u16.to_le_bytes());
        let manifest_key = encode_causal_manifest_key(collection, document, hash);
        let manifest =
            decode_causal_manifest(&manifest_key, &manifest_value, database, shard, 1, 1).unwrap();
        let change_key = descriptor_change_key(&manifest, desc).unwrap();
        assert_eq!(describe_change(&change_key, &change).unwrap(), desc);
        assert_eq!(&change_key[66..74], &1_u64.to_be_bytes());
        assert_eq!(&change_key[74..76], &1_u16.to_be_bytes());
        verify_descriptor_change(&manifest, desc, &change_key, &change).unwrap();

        let pointer = StatePointer {
            collection,
            document,
            manifest_sha256: hash,
        };
        let (pointer_key, pointer_value) = encode_state_pointer(pointer);
        assert_eq!(pointer_value.len(), 80);
        assert_eq!(
            decode_state_pointer(&pointer_key, &pointer_value).unwrap(),
            pointer
        );
        verify_state_pointer_manifest(pointer, &manifest).unwrap();
        assert!(
            verify_state_pointer_manifest(
                StatePointer {
                    manifest_sha256: [0; 32],
                    ..pointer
                },
                &manifest
            )
            .is_err()
        );
        assert!(
            verify_state_pointer_manifest(
                StatePointer {
                    document: DocumentId::from_bytes([11; 16]),
                    ..pointer
                },
                &manifest
            )
            .is_err()
        );
        let applied = AppliedIdentity {
            event,
            digest: [9; 32],
            epoch: MembershipEpoch::new(17),
            configuration: [10; 32],
        };
        let applied_key = encode_applied_key(event).unwrap();
        let applied_value = encode_applied_value(applied).unwrap();
        assert_eq!(&applied_key[34..42], &1_u64.to_be_bytes());
        assert_eq!(&applied_value[72..80], &1_u64.to_le_bytes());
        assert_eq!(&applied_value[112..120], &17_u64.to_le_bytes());
        assert_eq!(
            decode_applied_identity(&applied_key, &applied_value, database, shard).unwrap(),
            applied
        );
    }

    #[test]
    fn maximum_manifest_is_exact_and_caps_fail_closed() {
        let (database, shard, collection, document) = scope();
        let (value, key) = manifest_for(document, 1_024);
        assert_eq!(value.len(), CAUSAL_MANIFEST_MAX_VALUE_LEN);
        let decoded =
            decode_causal_manifest(&key, &value, database, shard, HARD_COUNT, HARD_COUNT).unwrap();
        assert_eq!(decoded.context.len(), 1_024);
        assert_eq!(decoded.siblings.len(), 1_024);
        assert!(decode_causal_manifest(&key, &value, database, shard, 1_023, HARD_COUNT).is_err());
        let context = (1..=1_024)
            .map(|n| DocumentDot {
                origin: origin(n),
                sequence: 1,
            })
            .collect::<Vec<_>>();
        let mut siblings = (1..=1_024)
            .map(|n| descriptor(n, database, shard))
            .collect::<Vec<_>>();
        siblings.push(descriptor(1_025, database, shard));
        assert!(
            encode_causal_manifest_into(
                &mut Vec::new(),
                collection,
                document,
                database,
                shard,
                &context,
                &siblings,
                HARD_COUNT,
                HARD_COUNT
            )
            .is_err()
        );
    }

    #[test]
    fn corrupted_or_mismatched_references_are_rejected() {
        let (database, shard, collection, document) = scope();
        let (value, key) = manifest_for(document, 1);
        let mut corrupt = value.clone();
        corrupt[52] ^= 1;
        assert!(matches!(
            decode_causal_manifest(&key, &corrupt, database, shard, 1, 1),
            Err(FormatError::ChecksumMismatch { .. })
        ));
        let mut wrong_key = key;
        wrong_key[34] ^= 1;
        assert!(decode_causal_manifest(&wrong_key, &value, database, shard, 1, 1).is_err());
        let mut wrong_ids = value.clone();
        wrong_ids[12] ^= 1;
        let end = wrong_ids.len() - 4;
        let checksum = crc32c::crc32c(&wrong_ids[..end]);
        wrong_ids[end..].copy_from_slice(&checksum.to_le_bytes());
        let wrong_hash: [u8; 32] = Sha256::digest(&wrong_ids).into();
        let matching_hash_key = encode_causal_manifest_key(collection, document, wrong_hash);
        assert!(
            decode_causal_manifest(&matching_hash_key, &wrong_ids, database, shard, 1, 1).is_err()
        );
        let manifest = decode_causal_manifest(&key, &value, database, shard, 1, 1).unwrap();
        let desc = manifest.siblings[0];
        let mut change_key = descriptor_change_key(&manifest, desc).unwrap();
        change_key[74] ^= 1;
        assert!(verify_descriptor_change(&manifest, desc, &change_key, &[0; 157]).is_err());
        let (pointer_key, mut pointer_value) = encode_state_pointer(StatePointer {
            collection,
            document,
            manifest_sha256: manifest.sha256,
        });
        pointer_value[76] ^= 1;
        assert!(decode_state_pointer(&pointer_key, &pointer_value).is_err());
        let applied = AppliedIdentity {
            event: desc.event,
            digest: [9; 32],
            epoch: MembershipEpoch::new(1),
            configuration: [10; 32],
        };
        let applied_key = encode_applied_key(desc.event).unwrap();
        let mut applied_value = encode_applied_value(applied).unwrap();
        applied_value[12] = 1;
        let checksum = crc32c::crc32c(&applied_value[..152]);
        applied_value[152..].copy_from_slice(&checksum.to_le_bytes());
        assert!(decode_applied_identity(&applied_key, &applied_value, database, shard).is_err());
        assert!(
            decode_applied_identity(
                &applied_key,
                &encode_applied_value(applied).unwrap(),
                database,
                ShardId::new(8)
            )
            .is_err()
        );
        assert!(
            encode_applied_value(AppliedIdentity {
                epoch: MembershipEpoch::new(0),
                ..applied
            })
            .is_err()
        );
        assert!(
            encode_applied_value(AppliedIdentity {
                configuration: [0; 32],
                ..applied
            })
            .is_err()
        );
    }

    #[test]
    fn checksum_valid_noncanonical_manifest_still_fails() {
        let (database, shard, collection, document) = scope();
        let (mut value, _) = manifest_for(document, 2);
        let first = value[48..88].to_vec();
        let second = value[88..128].to_vec();
        value[48..88].copy_from_slice(&second);
        value[88..128].copy_from_slice(&first);
        let end = value.len() - 4;
        let checksum = crc32c::crc32c(&value[..end]);
        value[end..].copy_from_slice(&checksum.to_le_bytes());
        let key = encode_causal_manifest_key(collection, document, Sha256::digest(&value).into());
        assert!(decode_causal_manifest(&key, &value, database, shard, 2, 2).is_err());

        let (mut duplicate_origin, _) = manifest_for(document, 2);
        let first_origin = duplicate_origin[48..80].to_vec();
        duplicate_origin[88..120].copy_from_slice(&first_origin);
        duplicate_origin[120..128].copy_from_slice(&2_u64.to_le_bytes());
        let end = duplicate_origin.len() - 4;
        let checksum = crc32c::crc32c(&duplicate_origin[..end]);
        duplicate_origin[end..].copy_from_slice(&checksum.to_le_bytes());
        let key = encode_causal_manifest_key(
            collection,
            document,
            Sha256::digest(&duplicate_origin).into(),
        );
        assert!(decode_causal_manifest(&key, &duplicate_origin, database, shard, 2, 2).is_err());
    }

    #[test]
    fn eleven_maximum_manifest_puts_fit_one_stage_frame_twelve_do_not() {
        let (_, _, _, _) = scope();
        let groups = (0..12_u8)
            .map(|number| {
                let mut bytes = [3; 16];
                bytes[15] = number;
                let (value, key) = manifest_for(DocumentId::from_bytes(bytes), 1_024);
                (key.to_vec(), value)
            })
            .collect::<Vec<_>>();
        let eleven = frame(&groups[..11]).unwrap();
        assert_eq!(eleven.len(), 2_062_769);
        assert_eq!(
            decode_wal_frame(&eleven, &FormatLimits::default())
                .unwrap()
                .operation_count(),
            11
        );
        assert!(frame(&groups).is_err());
    }

    #[test]
    fn serialized_source_and_receiver_frames_obey_exact_capacity_inequality() {
        let (database, shard, collection, document) = scope();
        let event = descriptor(1, database, shard).event;
        let (root_key, root_value) = encode_state_pointer(StatePointer {
            collection,
            document,
            manifest_sha256: [7; 32],
        });
        let change_key = encode_change_key(collection, document, event.origin, 1, 0).unwrap();
        // Frame serialization treats D1 as opaque; a near-cap source value
        // exercises the exact physical overhead without inventing a schema.
        let d1 = vec![8; 1_000_000];
        let metadata = vec![9; 256];
        let d5_key = encode_applied_key(event).unwrap();
        let d5 = encode_applied_value(AppliedIdentity {
            event,
            digest: [3; 32],
            epoch: MembershipEpoch::new(1),
            configuration: [4; 32],
        })
        .unwrap();
        let receiver = frame(&[
            (change_key.to_vec(), d1.clone()),
            (root_key.to_vec(), root_value.to_vec()),
            (d5_key.to_vec(), d5.to_vec()),
        ])
        .unwrap();
        let source_v2 = frame(&[
            (change_key.to_vec(), d1.clone()),
            (root_key.to_vec(), root_value.to_vec()),
            (vec![0xD2; 42], metadata.clone()),
            (vec![0xD3; 26], vec![0; 80]),
            (vec![0xD4; 34], vec![0; 24]),
        ])
        .unwrap();
        assert_eq!(receiver.len(), 267 + d1.len() + 208);
        assert_eq!(source_v2.len(), 293 + d1.len() + 208 + metadata.len());
        assert_eq!(source_v2.len() - receiver.len(), metadata.len() + 26);

        let binding = DocumentBinding {
            collection,
            document,
            generation: 3,
            schema_digest: [5; 32],
        };
        let mut old_inline = Vec::new();
        encode_state_into(
            &mut old_inline,
            collection,
            document,
            &[DocumentDot {
                origin: event.origin,
                sequence: 1,
            }],
            &[DocumentSibling {
                event,
                generation: binding.generation,
                schema_digest: binding.schema_digest,
                kind: DocumentKind::Tombstone,
                document_bytes: &[],
            }],
        )
        .unwrap();
        assert_eq!(old_inline.len(), 201);
        assert!(decode_state(&old_inline).is_ok());
        assert!(decode_state_pointer(&root_key, &old_inline).is_err());
        assert!(decode_state(&root_value).is_err());
        let mut real_change = Vec::new();
        encode_change_into(
            &mut real_change,
            event,
            binding,
            DocumentKind::Tombstone,
            &[],
        )
        .unwrap();
        let receiver_v1 = frame(&[
            (change_key.to_vec(), real_change.clone()),
            (root_key.to_vec(), root_value.to_vec()),
            (d5_key.to_vec(), d5.to_vec()),
        ])
        .unwrap();
        let source_v1 = frame(&[
            (change_key.to_vec(), real_change),
            (root_key.to_vec(), old_inline),
            (vec![0xD2; 42], metadata.clone()),
            (vec![0xD3; 26], vec![0; 80]),
            (vec![0xD4; 34], vec![0; 24]),
        ])
        .unwrap();
        assert_eq!(
            source_v1.len() - receiver_v1.len(),
            metadata.len() + 26 + 121
        );
        assert!(source_v1.len() <= tb_core::MAX_WAL_GROUP_BYTES);
        assert!(receiver_v1.len() <= tb_core::MAX_WAL_GROUP_BYTES);
    }

    #[test]
    fn exact_two_mib_source_frame_leaves_receiver_headroom() {
        let (database, shard, collection, document_a) = scope();
        let document_b = DocumentId::from_bytes([4; 16]);
        let event = descriptor(1, database, shard).event;
        let binding_a = DocumentBinding {
            collection,
            document: document_a,
            generation: 3,
            schema_digest: [5; 32],
        };
        let binding_b = DocumentBinding {
            collection,
            document: document_b,
            generation: 3,
            schema_digest: [5; 32],
        };
        let change_a = sized_live_change(binding_a, event, 1_048_576);
        let change_b = sized_live_change(binding_b, event, 1_047_611);
        let d1_a = (
            encode_change_key(collection, document_a, event.origin, 1, 0)
                .unwrap()
                .to_vec(),
            change_a,
        );
        let d1_b = (
            encode_change_key(collection, document_b, event.origin, 1, 1)
                .unwrap()
                .to_vec(),
            change_b,
        );
        let (key_a, value_a) = encode_state_pointer(StatePointer {
            collection,
            document: document_a,
            manifest_sha256: [7; 32],
        });
        let (key_b, value_b) = encode_state_pointer(StatePointer {
            collection,
            document: document_b,
            manifest_sha256: [8; 32],
        });
        let d0_a = (key_a.to_vec(), value_a.to_vec());
        let d0_b = (key_b.to_vec(), value_b.to_vec());
        let metadata = vec![9; 256];
        let mut source = vec![
            d1_a.clone(),
            d1_b.clone(),
            d0_a.clone(),
            d0_b.clone(),
            (vec![0xD2; 42], metadata),
            (vec![0xD3; 26], vec![0; 80]),
            (vec![0xD4; 34], vec![0; 24]),
        ];
        let d5_key = encode_applied_key(event).unwrap();
        let d5_value = encode_applied_value(AppliedIdentity {
            event,
            digest: [3; 32],
            epoch: MembershipEpoch::new(1),
            configuration: [4; 32],
        })
        .unwrap();
        let receiver = vec![d1_a, d1_b, d0_a, d0_b, (d5_key.to_vec(), d5_value.to_vec())];
        let source_frame = frame(&source).unwrap();
        let receiver_frame = frame(&receiver).unwrap();
        assert_eq!(source_frame.len(), tb_core::MAX_WAL_GROUP_BYTES);
        assert_eq!(receiver_frame.len(), 2_096_870);
        assert_eq!(source_frame.len() - receiver_frame.len(), 256 + 26);
        source[4].1.push(9);
        assert!(frame(&source).is_err());
    }

    #[test]
    fn source_and_receiver_operation_edges_serialize() {
        let (database, shard, collection, _) = scope();
        let event = descriptor(1, database, shard).event;
        let mut source = Vec::new();
        let mut receiver = Vec::new();
        let mut exact_d1_bytes = 0;
        for ordinal in 0..510_u16 {
            let mut document_bytes = [0; 16];
            document_bytes[14..].copy_from_slice(&ordinal.to_be_bytes());
            let document = DocumentId::from_bytes(document_bytes);
            let binding = DocumentBinding {
                collection,
                document,
                generation: 3,
                schema_digest: [5; 32],
            };
            let mut change = Vec::new();
            encode_change_into(&mut change, event, binding, DocumentKind::Tombstone, &[]).unwrap();
            exact_d1_bytes += change.len();
            let d1 = (
                encode_change_key(collection, document, event.origin, 1, ordinal)
                    .unwrap()
                    .to_vec(),
                change,
            );
            let (d0_key, d0_value) = encode_state_pointer(StatePointer {
                collection,
                document,
                manifest_sha256: [7; 32],
            });
            let d0 = (d0_key.to_vec(), d0_value.to_vec());
            source.extend([d1.clone(), d0.clone()]);
            receiver.extend([d1, d0]);
        }
        let metadata = vec![9; 256];
        source.extend([
            (vec![0xD2; 42], metadata.clone()),
            (vec![0xD3; 26], vec![0; 80]),
            (vec![0xD4; 34], vec![0; 24]),
        ]);
        let applied_key = encode_applied_key(event).unwrap();
        let applied_value = encode_applied_value(AppliedIdentity {
            event,
            digest: [3; 32],
            epoch: MembershipEpoch::new(1),
            configuration: [4; 32],
        })
        .unwrap();
        receiver.push((applied_key.to_vec(), applied_value.to_vec()));
        let source_frame = frame(&source).unwrap();
        let receiver_frame = frame(&receiver).unwrap();
        assert_eq!(
            decode_wal_frame(&source_frame, &FormatLimits::default())
                .unwrap()
                .operation_count(),
            1_023
        );
        assert_eq!(
            decode_wal_frame(&receiver_frame, &FormatLimits::default())
                .unwrap()
                .operation_count(),
            1_021
        );
        assert_eq!(
            source_frame.len(),
            293 + exact_d1_bytes + 208 * 510 + metadata.len()
        );
        assert_eq!(receiver_frame.len(), 267 + exact_d1_bytes + 208 * 510);
        assert_eq!(
            source_frame.len() - receiver_frame.len(),
            metadata.len() + 26
        );
        let ordinal = 510_u16;
        let mut document_bytes = [0; 16];
        document_bytes[14..].copy_from_slice(&ordinal.to_be_bytes());
        let document = DocumentId::from_bytes(document_bytes);
        let binding = DocumentBinding {
            collection,
            document,
            generation: 3,
            schema_digest: [5; 32],
        };
        let mut change = Vec::new();
        encode_change_into(&mut change, event, binding, DocumentKind::Tombstone, &[]).unwrap();
        let (key, value) = encode_state_pointer(StatePointer {
            collection,
            document,
            manifest_sha256: [7; 32],
        });
        source.extend([
            (
                encode_change_key(collection, document, event.origin, 1, ordinal)
                    .unwrap()
                    .to_vec(),
                change,
            ),
            (key.to_vec(), value.to_vec()),
        ]);
        assert_eq!(source.len(), 1_025);
        assert!(frame(&source).is_err());
    }
}
