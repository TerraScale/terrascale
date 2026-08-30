//! Internal keys, keyspaces, and entries of the LSM storage engine (P1).
//!
//! Everything in this module is private to the storage implementation. The
//! LSM packs four logical keyspaces into one ordered key space:
//!
//! - `Documents` — the physical document records, keyed
//!   `tenant > collection > document` through the shared storage codec, so
//!   canonical LSM key order equals the engine's canonical document order;
//! - `Ledger` — the durable operation-resolution records, keyed
//!   `(tenant, operation)`;
//! - `History` — the publication-history records, keyed by global storage
//!   sequence in big-endian order so sequence order equals key order;
//! - `Meta` — small engine-internal markers (root chain head, high-water).
//!
//! A key plus a sequence and an entry kind forms the full internal key: user
//! keys order ascending, sequences order descending, so the newest version of
//! a user key is always the first a merged scan observes.

use crate::types::{CollectionName, DocumentId, OperationId, TenantId};

use super::super::codec;

/// One logical keyspace packed into the ordered LSM key space.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(super) enum Keyspace {
    /// Physical document records.
    Documents,
    /// Durable operation-resolution records.
    Ledger,
    /// Publication-history records by sequence.
    History,
    /// Engine-internal markers.
    Meta,
}

impl Keyspace {
    /// The keyspace's one-byte tag. Tags order keyspaces deterministically.
    pub(super) const fn tag(self) -> u8 {
        match self {
            Self::Documents => 1,
            Self::Ledger => 2,
            Self::History => 3,
            Self::Meta => 4,
        }
    }
}

/// The full internal key of one entry: keyspace, user key, sequence, kind.
///
/// Ordering is by user key ascending, then sequence descending, so the
/// newest version of a user key is always the first a merged scan observes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct InternalKey {
    /// The encoded user key, prefixed by the keyspace tag.
    bytes: Vec<u8>,
    /// The commit sequence this entry was written at.
    sequence: u64,
    /// Whether this entry is a deletion marker. A deletion orders after a
    /// put at the same user key and sequence, which cannot occur in practice
    /// because one commit writes at most one entry per user key; the order
    /// exists so the ordering is total.
    deletion: bool,
}

impl Ord for InternalKey {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.bytes
            .cmp(&other.bytes)
            .then_with(|| other.sequence.cmp(&self.sequence))
            .then_with(|| self.deletion.cmp(&other.deletion))
    }
}

impl PartialOrd for InternalKey {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

/// One stored entry value.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(super) struct Entry {
    /// The entry's full internal key.
    pub(super) key: InternalKey,
    /// The encoded value bytes.
    pub(super) value: Vec<u8>,
}

/// Appends one identifier in an order-preserving encoding: the NUL
/// terminator sorts below every legal identifier character (identifiers
/// reject control characters), so byte order equals `(tenant, collection,
/// document)` lexicographic order — the engine's canonical key order.
/// Length-prefixed encoding would order by length first and violate it.
pub(super) fn push_identifier(out: &mut Vec<u8>, value: &str) {
    out.extend_from_slice(value.as_bytes());
    out.push(0);
}

impl InternalKey {
    /// Builds the documents-keyspace key of one stored document.
    pub(super) fn document(
        tenant: &TenantId,
        collection: &CollectionName,
        document: &DocumentId,
        sequence: u64,
    ) -> Self {
        let mut bytes = Vec::new();
        codec::push_u8(&mut bytes, Keyspace::Documents.tag());
        push_identifier(&mut bytes, tenant.as_str());
        push_identifier(&mut bytes, collection.as_str());
        push_identifier(&mut bytes, document.as_str());
        Self { bytes, sequence, deletion: false }
    }

    /// Builds a documents-keyspace deletion marker for one key.
    pub(super) fn document_delete(
        tenant: &TenantId,
        collection: &CollectionName,
        document: &DocumentId,
        sequence: u64,
    ) -> Self {
        let mut bytes = Vec::new();
        codec::push_u8(&mut bytes, Keyspace::Documents.tag());
        push_identifier(&mut bytes, tenant.as_str());
        push_identifier(&mut bytes, collection.as_str());
        push_identifier(&mut bytes, document.as_str());
        Self { bytes, sequence, deletion: true }
    }

    /// Builds the ledger-keyspace key of one operation identity.
    pub(super) fn ledger(tenant: &TenantId, operation: &str, sequence: u64) -> Self {
        let mut bytes = Vec::new();
        codec::push_u8(&mut bytes, Keyspace::Ledger.tag());
        codec::push_str(&mut bytes, tenant.as_str());
        codec::push_str(&mut bytes, operation);
        Self { bytes, sequence, deletion: false }
    }

    /// Builds the history-keyspace key of one published commit.
    pub(super) fn history(sequence: u64) -> Self {
        let mut bytes = Vec::new();
        codec::push_u8(&mut bytes, Keyspace::History.tag());
        bytes.extend_from_slice(&sequence.to_be_bytes());
        Self { bytes, sequence, deletion: false }
    }

    /// Builds a meta-keyspace key for one label.
    pub(super) fn meta(label: &str, sequence: u64) -> Self {
        let mut bytes = Vec::new();
        codec::push_u8(&mut bytes, Keyspace::Meta.tag());
        codec::push_str(&mut bytes, label);
        Self { bytes, sequence, deletion: false }
    }

    /// Builds the exclusive upper bound for every sequence of one user key:
    /// the first key greater than every entry of that user key at or below
    /// `sequence`.
    pub(super) fn user_key_upper_bound(&self) -> Vec<u8> {
        let mut next = self.bytes.clone();
        while next.last() == Some(&0xFF) {
            next.pop();
        }
        if let Some(byte) = next.last_mut() {
            *byte += 1;
        } else {
            next.push(0);
        }
        next
    }

    /// Returns the encoded key bytes without the sequence.
    pub(super) fn user_key(&self) -> &[u8] {
        &self.bytes
    }

    /// Returns the keyspace tag byte.
    pub(super) fn tag(&self) -> u8 {
        self.bytes.first().copied().unwrap_or(0)
    }

    /// Returns the entry's commit sequence.
    pub(super) fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Reports whether this entry is a deletion marker.
    pub(super) fn is_deletion(&self) -> bool {
        self.deletion
    }

    /// Decodes and semantically validates a key read from a segment or WAL
    /// frame. A checksummed frame is not trusted to invent unknown keyspaces,
    /// malformed identifiers, or a history key that disagrees with its
    /// internal sequence.
    pub(super) fn decode(bytes: &[u8], sequence: u64, deletion: bool) -> Option<Self> {
        let tag = *bytes.first()?;
        let valid = if tag == Keyspace::Documents.tag() {
            valid_document_key(&bytes[1..])
        } else if tag == Keyspace::Ledger.tag() {
            !deletion && valid_ledger_key(&bytes[1..])
        } else if tag == Keyspace::History.tag() {
            !deletion
                && bytes.len() == 9
                && u64::from_be_bytes(bytes[1..].try_into().ok()?) == sequence
        } else if tag == Keyspace::Meta.tag() {
            !deletion && valid_meta_key(&bytes[1..])
        } else {
            false
        };
        valid.then(|| Self { bytes: bytes.to_vec(), sequence, deletion })
    }
}

fn valid_document_key(bytes: &[u8]) -> bool {
    let parts = bytes.split(|byte| *byte == 0).collect::<Vec<_>>();
    if parts.len() != 4 || !parts[3].is_empty() {
        return false;
    }
    let Ok(tenant) = std::str::from_utf8(parts[0]) else {
        return false;
    };
    let Ok(collection) = std::str::from_utf8(parts[1]) else {
        return false;
    };
    let Ok(document) = std::str::from_utf8(parts[2]) else {
        return false;
    };
    TenantId::try_new(tenant).is_ok()
        && CollectionName::try_new(collection).is_ok()
        && DocumentId::try_new(document).is_ok()
}

fn valid_ledger_key(bytes: &[u8]) -> bool {
    let mut reader = codec::Reader::new(bytes);
    let Some(tenant) = reader.read_string() else {
        return false;
    };
    let Some(operation) = reader.read_string() else {
        return false;
    };
    reader.remaining() == 0
        && TenantId::try_new(&tenant).is_ok()
        && OperationId::try_new(&operation).is_ok()
}

fn valid_meta_key(bytes: &[u8]) -> bool {
    let mut reader = codec::Reader::new(bytes);
    let Some(label) = reader.read_string() else {
        return false;
    };
    reader.remaining() == 0 && !label.is_empty() && label.len() <= 4096
}

/// The exclusive upper bound of the documents keyspace for one tenant: the
/// first key past every document the tenant owns.
///
/// The tenant's keys continue with a NUL separator and a collection name;
/// a `0x01` byte directly after the tenant text sorts above every one of
/// them and below every other tenant's keyspace.
pub(super) fn tenant_upper_bound(tenant: &TenantId) -> Vec<u8> {
    let mut bound = Vec::new();
    codec::push_u8(&mut bound, Keyspace::Documents.tag());
    bound.extend_from_slice(tenant.as_str().as_bytes());
    bound.push(1);
    bound
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tenant(name: &str) -> TenantId {
        TenantId::try_new(name).expect("valid tenant")
    }

    fn collection(name: &str) -> CollectionName {
        CollectionName::try_new(name).expect("valid collection")
    }

    fn document(name: &str) -> DocumentId {
        DocumentId::try_new(name).expect("valid document")
    }

    #[test]
    fn newer_sequences_order_first_for_one_user_key() {
        let old = InternalKey::document(&tenant("t"), &collection("c"), &document("d"), 1);
        let new = InternalKey::document(&tenant("t"), &collection("c"), &document("d"), 2);
        assert!(new < old, "descending sequence puts the newest entry first");
    }

    #[test]
    fn user_keys_order_by_tenant_collection_document() {
        let first = InternalKey::document(&tenant("a"), &collection("c"), &document("d"), 1);
        let second = InternalKey::document(&tenant("b"), &collection("a"), &document("a"), 1);
        assert!(first < second);
    }

    #[test]
    fn history_keys_order_by_sequence() {
        assert!(InternalKey::history(1) < InternalKey::history(2));
        assert!(InternalKey::history(u64::from(u32::MAX)) < InternalKey::history(u64::MAX));
    }

    #[test]
    fn upper_bound_is_exclusive_and_covers_every_sequence() {
        let key = InternalKey::document(&tenant("t"), &collection("c"), &document("d"), 7);
        let bound = key.user_key_upper_bound();
        assert!(key.user_key() < bound.as_slice());
        let other = InternalKey::document(&tenant("t"), &collection("c"), &document("d"), u64::MAX);
        assert!(other.user_key() < bound.as_slice());
    }

    #[test]
    fn tenant_upper_bound_covers_every_collection() {
        let bound = tenant_upper_bound(&tenant("t"));
        let key = InternalKey::document(&tenant("t"), &collection("zzz"), &document("d"), 1);
        assert!(key.user_key() < bound.as_slice());
        let foreign = InternalKey::document(&tenant("u"), &collection("a"), &document("a"), 1);
        assert!(bound.as_slice() < foreign.user_key());
    }

    #[test]
    fn hostile_decoded_keys_reject_unknown_or_inconsistent_semantics() {
        assert!(InternalKey::decode(&[0xff], 1, false).is_none());
        assert!(
            InternalKey::decode(&[Keyspace::Documents.tag(), b't', 0, b'c', 0], 1, false).is_none()
        );

        let mut ledger = InternalKey::ledger(&tenant("t"), "op", 1).user_key().to_vec();
        ledger.push(0);
        assert!(InternalKey::decode(&ledger, 1, false).is_none());
        assert!(
            InternalKey::decode(InternalKey::ledger(&tenant("t"), "op", 1).user_key(), 1, true)
                .is_none()
        );

        assert!(InternalKey::decode(InternalKey::history(7).user_key(), 8, false).is_none());
        assert!(InternalKey::decode(InternalKey::history(7).user_key(), 7, true).is_none());
    }
}
