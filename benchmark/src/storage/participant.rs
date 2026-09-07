//! Immutable participant preparation records for cross-file storage commits.
use crate::types::{CollectionName, Document, DocumentId, ExpectedVersion, OperationId, StorageSequence, TenantId, DocumentValue};
use crate::storage::{PrivateMetadataWrite, SchemaEnvelopeWrite};

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
    pub(crate) metadata: Vec<PrivateMetadataWrite>,
    pub(crate) schema_envelopes: Vec<SchemaEnvelopeWrite>,
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantPrepareReceipt { pub(crate) identity: ParticipantIdentity, pub(crate) writes: Vec<ParticipantWrite>, pub(crate) metadata: Vec<PrivateMetadataWrite>, pub(crate) schema_envelopes: Vec<SchemaEnvelopeWrite> }

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

/// Encodes all staged writes with bounded canonical framing.
pub(crate) fn encode_prepare_payload(writes: &[ParticipantWrite], metadata: &[PrivateMetadataWrite], schemas: &[SchemaEnvelopeWrite]) -> Result<Vec<u8>, ()> {
    if writes.len() > 1024 { return Err(()); }
    let mut out = vec![2u8];
    out.extend_from_slice(&u32::try_from(writes.len()).map_err(|_| ())?.to_be_bytes());
    for write in writes { let encoded = encode_write(write)?; out.extend_from_slice(&u32::try_from(encoded.len()).map_err(|_| ())?.to_be_bytes()); out.extend_from_slice(&encoded); }
    out.extend_from_slice(&u32::try_from(metadata.len()).map_err(|_| ())?.to_be_bytes());
    for change in metadata { crate::storage::codec::push_str(&mut out, &change.key); out.push(u8::from(change.value.is_some())); if let Some(value) = &change.value { out.extend_from_slice(&u32::try_from(crate::storage::codec::encode_private_value(value).ok_or(())?.len()).map_err(|_| ())?.to_be_bytes()); out.extend_from_slice(&crate::storage::codec::encode_private_value(value).ok_or(())?); } }
    out.extend_from_slice(&u32::try_from(schemas.len()).map_err(|_| ())?.to_be_bytes());
    for schema in schemas { crate::storage::codec::push_str(&mut out, &schema.database); crate::storage::codec::push_str(&mut out, &schema.branch); crate::storage::codec::push_str(&mut out, &schema.collection); out.push(u8::from(schema.expected.is_some())); if let Some(expected) = &schema.expected { out.extend_from_slice(&u32::try_from(expected.len()).map_err(|_| ())?.to_be_bytes()); out.extend_from_slice(expected); } out.extend_from_slice(&u32::try_from(schema.replacement.len()).map_err(|_| ())?.to_be_bytes()); out.extend_from_slice(&schema.replacement); }
    (out.len() <= 64 * 1024 * 1024).then_some(out).ok_or(())
}

/// Decodes a complete framed participant payload.
pub(crate) fn decode_prepare_payload(bytes: &[u8]) -> Result<(Vec<ParticipantWrite>, Vec<PrivateMetadataWrite>, Vec<SchemaEnvelopeWrite>), ()> {
    if bytes.len() > 64 * 1024 * 1024 || bytes.first() != Some(&2) { return Err(()); }
    let mut pos = 1usize; if bytes.len() < pos + 4 { return Err(()); }
    let count = u32::from_be_bytes(bytes[pos..pos+4].try_into().map_err(|_| ())?) as usize; pos += 4;
    if count > 1024 { return Err(()); }
    let mut out = Vec::with_capacity(count);
    for _ in 0..count { if bytes.len() < pos+4 { return Err(()); } let n=u32::from_be_bytes(bytes[pos..pos+4].try_into().map_err(|_| ())?) as usize; pos+=4; let end=pos.checked_add(n).ok_or(())?; if end>bytes.len(){return Err(());} out.push(decode_write(&bytes[pos..end])?); pos=end; }
    let mut r = crate::storage::codec::Reader::new(&bytes[pos..]);
    let metadata_count = usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; if metadata_count > 1024 { return Err(()); }
    let mut metadata = Vec::with_capacity(metadata_count);
    for _ in 0..metadata_count { let key = r.read_string().ok_or(())?; let value = match r.read_u8().ok_or(())? { 0 => None, 1 => { let n=usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; Some(crate::storage::codec::decode_private_value(r.take(n).ok_or(())?).ok_or(())?) }, _ => return Err(()) }; metadata.push(PrivateMetadataWrite { key, value }); }
    let schema_count = usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; if schema_count > 1024 { return Err(()); }
    let mut schemas = Vec::with_capacity(schema_count);
    for _ in 0..schema_count { let database=r.read_string().ok_or(())?; let branch=r.read_string().ok_or(())?; let collection=r.read_string().ok_or(())?; let expected=match r.read_u8().ok_or(())? { 0=>None, 1=>{let n=usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; Some(r.take(n).ok_or(())?.to_vec())}, _=>return Err(())}; let n=usize::try_from(r.read_u32().ok_or(())?).map_err(|_| ())?; let replacement=r.take(n).ok_or(())?.to_vec(); schemas.push(SchemaEnvelopeWrite { database, branch, collection, expected, replacement }); }
    if r.remaining() != 0 { return Err(()); } Ok((out, metadata, schemas))
}
