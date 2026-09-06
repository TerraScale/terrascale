//! Bounded full-publication wire codec, independent of the small lane envelope.
use super::{EngineError, ReplicatedPublication, ReplicatedRecord};
use crate::types::{CollectionName, DocumentId, DocumentValue};
const MAX_BYTES: usize = 64 * 1024 * 1024;
fn invalid() -> EngineError {
    EngineError::InvalidDocument
}
fn field(out: &mut Vec<u8>, bytes: &[u8]) -> Result<(), EngineError> {
    if out.len().saturating_add(bytes.len()).saturating_add(4) > MAX_BYTES {
        return Err(invalid());
    }
    out.extend_from_slice(&u32::try_from(bytes.len()).map_err(|_| invalid())?.to_be_bytes());
    out.extend_from_slice(bytes);
    Ok(())
}
fn take<'a>(bytes: &mut &'a [u8], len: usize) -> Result<&'a [u8], EngineError> {
    let value = bytes.get(..len).ok_or_else(invalid)?;
    *bytes = &bytes[len..];
    Ok(value)
}
fn take_field<'a>(bytes: &mut &'a [u8]) -> Result<&'a [u8], EngineError> {
    let length = u32::from_be_bytes(take(bytes, 4)?.try_into().map_err(|_| invalid())?) as usize;
    take(bytes, length)
}
fn text(bytes: &[u8]) -> Result<String, EngineError> {
    String::from_utf8(bytes.to_vec()).map_err(|_| invalid())
}
impl ReplicatedPublication {
    /// Encodes one complete bounded source transaction for fragmentation.
    /// # Errors
    /// Rejects invalid publication fields or encoded groups beyond 64 MiB.
    pub fn encode_wire(&self) -> Result<Vec<u8>, EngineError> {
        self.fingerprint()?;
        let mut out = b"TBGP1".to_vec();
        field(&mut out, self.origin.as_bytes())?;
        field(&mut out, self.commit_ulid.as_bytes())?;
        field(&mut out, &self.context)?;
        field(&mut out, &self.private_payload)?;
        out.extend_from_slice(&self.origin_sequence.to_be_bytes());
        out.extend_from_slice(&self.hlc.to_be_bytes());
        out.extend_from_slice(
            &u32::try_from(self.records.len()).map_err(|_| invalid())?.to_be_bytes(),
        );
        for record in &self.records {
            field(&mut out, record.collection.as_str().as_bytes())?;
            field(&mut out, record.id.as_str().as_bytes())?;
            out.push(u8::from(record.deleted));
            let body = if record.deleted {
                Vec::new()
            } else {
                crate::storage::encode_private_value(&record.body).ok_or_else(invalid)?
            };
            field(&mut out, &body)?;
        }
        if out.len() > MAX_BYTES {
            return Err(invalid());
        }
        Ok(out)
    }
    /// Decodes exactly one complete transaction, rejecting trailing bytes.
    /// # Errors
    /// Rejects malformed, oversized or inconsistent causal source data.
    pub fn decode_wire(mut bytes: &[u8]) -> Result<Self, EngineError> {
        if bytes.len() > MAX_BYTES || take(&mut bytes, 5)? != b"TBGP1" {
            return Err(invalid());
        }
        let origin = text(take_field(&mut bytes)?)?;
        let commit_ulid = text(take_field(&mut bytes)?)?;
        let context = take_field(&mut bytes)?.to_vec();
        let private_payload = take_field(&mut bytes)?.to_vec();
        let origin_sequence =
            u64::from_be_bytes(take(&mut bytes, 8)?.try_into().map_err(|_| invalid())?);
        let hlc = u64::from_be_bytes(take(&mut bytes, 8)?.try_into().map_err(|_| invalid())?);
        let count =
            u32::from_be_bytes(take(&mut bytes, 4)?.try_into().map_err(|_| invalid())?) as usize;
        if count > 100_000 || count > bytes.len() / 13 {
            return Err(invalid());
        }
        let mut records = Vec::with_capacity(count);
        for _ in 0..count {
            let collection =
                CollectionName::try_new(&text(take_field(&mut bytes)?)?).map_err(|_| invalid())?;
            let id = DocumentId::try_new(&text(take_field(&mut bytes)?)?).map_err(|_| invalid())?;
            let deleted = match take(&mut bytes, 1)?[0] {
                0 => false,
                1 => true,
                _ => return Err(invalid()),
            };
            let raw = take_field(&mut bytes)?;
            let body = if deleted {
                if !raw.is_empty() {
                    return Err(invalid());
                }
                DocumentValue::Null
            } else {
                crate::storage::decode_private_value(raw).ok_or_else(invalid)?
            };
            records.push(ReplicatedRecord {
                collection,
                id,
                body,
                deleted,
                origin: origin.clone(),
                origin_sequence,
                hlc,
                commit_ulid: commit_ulid.clone(),
                context: context.clone(),
            });
        }
        if !bytes.is_empty() {
            return Err(invalid());
        }
        let publication =
            Self { origin, origin_sequence, hlc, commit_ulid, context, private_payload, records };
        publication.fingerprint()?;
        Ok(publication)
    }
}
