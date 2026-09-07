//! Immutable participant preparation records for cross-file storage commits.
use crate::types::{CollectionName, Document, DocumentId, ExpectedVersion, OperationId, StorageSequence, TenantId, DocumentValue};

#[derive(Clone, Debug)]
pub(crate) enum ParticipantWrite {
    Put { document: Document, expected: ExpectedVersion },
    Delete { collection: Option<CollectionName>, id: DocumentId, expected: ExpectedVersion },
    ReplicatedPut { document: Document, commit_ulid: String, hlc: u64, origin: String, origin_sequence: u64, context: Vec<u8> },
    ReplicatedDelete { collection: CollectionName, id: DocumentId, commit_ulid: String, hlc: u64, origin: String, origin_sequence: u64, context: Vec<u8> },
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantIdentity {
    pub(crate) tenant: TenantId,
    pub(crate) database_incarnation: u128,
    pub(crate) operation: OperationId,
    pub(crate) participant: String,
    pub(crate) owner: String,
    pub(crate) digest: [u8; 32],
    pub(crate) epoch: u64,
    pub(crate) snapshot: StorageSequence,
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantPrepare {
    pub(crate) identity: ParticipantIdentity,
    pub(crate) writes: Vec<ParticipantWrite>,
    pub(crate) metadata: Vec<(String, Option<DocumentValue>)>,
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantPrepareReceipt { pub(crate) identity: ParticipantIdentity }

/// Encodes a bounded participant write using the storage canonical document form.
pub(crate) fn encode_write(write: &ParticipantWrite) -> Result<Vec<u8>, ()> {
    let mut out = vec![1u8];
    match write {
        ParticipantWrite::Put { document, expected } => {
            out.push(1); crate::storage::codec::push_str(&mut out, document.collection().as_str()); crate::storage::codec::push_str(&mut out, document.id().as_str()); crate::storage::codec::encode_expected(&mut out, *expected); crate::storage::codec::encode_document(&mut out, document);
        }
        ParticipantWrite::Delete { collection, id, expected } => {
            out.push(2); crate::storage::codec::push_str(&mut out, collection.as_ref().map_or("", |v| v.as_str())); crate::storage::codec::push_str(&mut out, id.as_str()); crate::storage::codec::encode_expected(&mut out, *expected);
        }
        ParticipantWrite::ReplicatedPut { document, commit_ulid, hlc, origin, origin_sequence, context } => {
            out.push(3); crate::storage::codec::push_str(&mut out, document.collection().as_str()); crate::storage::codec::push_str(&mut out, document.id().as_str()); crate::storage::codec::push_str(&mut out, commit_ulid); crate::storage::codec::push_u64(&mut out, *hlc); crate::storage::codec::push_str(&mut out, origin); crate::storage::codec::push_u64(&mut out, *origin_sequence); crate::storage::codec::push_u32(&mut out, context.len().try_into().map_err(|_| ())?); out.extend_from_slice(context); crate::storage::codec::encode_document(&mut out, document);
        }
        ParticipantWrite::ReplicatedDelete { collection, id, commit_ulid, hlc, origin, origin_sequence, context } => {
            out.push(4); crate::storage::codec::push_str(&mut out, collection.as_str()); crate::storage::codec::push_str(&mut out, id.as_str()); crate::storage::codec::push_str(&mut out, commit_ulid); crate::storage::codec::push_u64(&mut out, *hlc); crate::storage::codec::push_str(&mut out, origin); crate::storage::codec::push_u64(&mut out, *origin_sequence); crate::storage::codec::push_u32(&mut out, context.len().try_into().map_err(|_| ())?); out.extend_from_slice(context);
        }
    }
    (out.len() <= 1 << 20).then_some(out).ok_or(())
}

/// Decodes one canonical participant write and rejects trailing bytes.
pub(crate) fn decode_write(bytes: &[u8]) -> Result<ParticipantWrite, ()> {
    if bytes.len() > (1 << 20) || bytes.first() != Some(&1) { return Err(()); }
    let mut r = crate::storage::codec::Reader::new(&bytes[1..]);
    let tag = r.read_u8().ok_or(())?;
    let text = |r: &mut crate::storage::codec::Reader<'_>| r.read_string().ok_or(());
    let expected = |r: &mut crate::storage::codec::Reader<'_>| match r.read_u8().ok_or(())? { 0 => Ok(crate::types::ExpectedVersion::Any), 1 => Ok(crate::types::ExpectedVersion::New), 2 => Ok(crate::types::ExpectedVersion::Exact(r.read_u64().ok_or(())?)), _ => Err(()) };
    let out = match tag {
        1 => { let c=crate::types::CollectionName::try_new(&text(&mut r)?).map_err(|_| ())?; let id=crate::types::DocumentId::try_new(&text(&mut r)?).map_err(|_| ())?; let e=expected(&mut r)?; let d=crate::storage::codec::decode_document(&mut r).ok_or(())?; if d.collection() != &c || d.id() != &id { return Err(()); } ParticipantWrite::Put{document:d,expected:e} },
        2 => { let c=text(&mut r)?; let id=crate::types::DocumentId::try_new(&text(&mut r)?).map_err(|_| ())?; let e=expected(&mut r)?; ParticipantWrite::Delete{collection:if c.is_empty(){None}else{Some(crate::types::CollectionName::try_new(&c).map_err(|_| ())?)},id,expected:e} },
        3 => { let c=crate::types::CollectionName::try_new(&text(&mut r)?).map_err(|_| ())?; let id=crate::types::DocumentId::try_new(&text(&mut r)?).map_err(|_| ())?; let commit_ulid=text(&mut r)?; let hlc=r.read_u64().ok_or(())?; let origin=text(&mut r)?; let origin_sequence=r.read_u64().ok_or(())?; let n=usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; let context=r.take(n).ok_or(())?.to_vec(); let document=crate::storage::codec::decode_document(&mut r).ok_or(())?; if document.collection()!=&c || document.id()!=&id { return Err(()); } ParticipantWrite::ReplicatedPut{document,commit_ulid,hlc,origin,origin_sequence,context} },
        4 => { let collection=crate::types::CollectionName::try_new(&text(&mut r)?).map_err(|_| ())?; let id=crate::types::DocumentId::try_new(&text(&mut r)?).map_err(|_| ())?; let commit_ulid=text(&mut r)?; let hlc=r.read_u64().ok_or(())?; let origin=text(&mut r)?; let origin_sequence=r.read_u64().ok_or(())?; let n=usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; let context=r.take(n).ok_or(())?.to_vec(); ParticipantWrite::ReplicatedDelete{collection,id,commit_ulid,hlc,origin,origin_sequence,context} },
        _ => return Err(()),
    };
    (r.remaining() == 0).then_some(out).ok_or(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_invalid_version_tag_and_trailing_bytes() {
        assert!(decode_write(&[]).is_err());
        assert!(decode_write(&[2, 1]).is_err());
        assert!(decode_write(&[1, 9]).is_err());
        assert!(decode_write(&[1, 1, 0]).is_err());
    }

    #[test]
    fn rejects_oversized_encoded_payload() {
        let mut bytes = vec![1, 4];
        bytes.extend_from_slice(&[0; (1 << 20)]);
        assert!(decode_write(&bytes).is_err());
    }
}

#[cfg(test)]
mod roundtrip_tests {
    use super::*;
    use crate::types::ExpectedVersion;

    fn doc() -> Document {
        Document::new(
            DocumentId::try_new("doc-1").unwrap(),
            CollectionName::try_new("items").unwrap(),
            DocumentValue::Object(std::collections::BTreeMap::new()),
        )
    }

    #[test]
    fn put_roundtrip_preserves_canonical_bytes() {
        let value = ParticipantWrite::Put { document: doc(), expected: ExpectedVersion::Exact(7) };
        let encoded = encode_write(&value).unwrap();
        let decoded = decode_write(&encoded).unwrap();
        assert_eq!(encode_write(&decoded).unwrap(), encoded);
    }

    #[test]
    fn delete_roundtrip_preserves_canonical_bytes() {
        let value = ParticipantWrite::Delete { collection: None, id: DocumentId::try_new("doc-1").unwrap(), expected: ExpectedVersion::Any };
        let encoded = encode_write(&value).unwrap();
        let decoded = decode_write(&encoded).unwrap();
        assert_eq!(encode_write(&decoded).unwrap(), encoded);
    }

    #[test]
    fn replicated_put_roundtrip_preserves_canonical_bytes() {
        let value = ParticipantWrite::ReplicatedPut { document: doc(), commit_ulid: "commit".into(), hlc: 9, origin: "origin".into(), origin_sequence: 3, context: vec![1, 2, 3] };
        let encoded = encode_write(&value).unwrap();
        let decoded = decode_write(&encoded).unwrap();
        assert_eq!(encode_write(&decoded).unwrap(), encoded);
    }

    #[test]
    fn replicated_delete_roundtrip_preserves_canonical_bytes() {
        let value = ParticipantWrite::ReplicatedDelete { collection: CollectionName::try_new("items").unwrap(), id: DocumentId::try_new("doc-1").unwrap(), commit_ulid: "commit".into(), hlc: 9, origin: "origin".into(), origin_sequence: 3, context: vec![1, 2, 3] };
        let encoded = encode_write(&value).unwrap();
        let decoded = decode_write(&encoded).unwrap();
        assert_eq!(encode_write(&decoded).unwrap(), encoded);
    }
}
