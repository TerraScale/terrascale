//! Signed backend observations of a completed native restore.
//!
//! A signature attests to measured facts; it does not authorize a restore.
//! Consumers must pin the key and compare the receipt to their owned request.

use aws_lc_rs::signature::{ED25519, Ed25519KeyPair, UnparsedPublicKey};
use base64::{Engine as _, engine::general_purpose::STANDARD};
use serde::{Deserialize, Serialize};
use terrabase_cluster::production::DatabaseRouteScope;

/// Versioned signature domain, including the terminal NUL byte.
pub const NATIVE_RECOVERY_SIGNATURE_DOMAIN: &[u8] = b"terrascale/native-recovery-receipt/v1\0";
/// Maximum serialized signed envelope length.
pub const MAX_NATIVE_RECOVERY_RECEIPT_BYTES: usize = 16 * 1024;

/// Canonical v1 payload; field declaration order is the producer's JSON order.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeRecoveryReceipt {
    /// Wire schema, currently one.
    pub schema_version: u32,
    /// Owned tenant identifier.
    pub tenant_id: String,
    /// Owned project identifier.
    pub project_id: String,
    /// Database from which the native recovery point was captured.
    pub source_database_id: String,
    /// Database into which the native recovery point was restored.
    pub target_database_id: String,
    /// Durable source journal boundary.
    pub source_journal_cut: u64,
    /// Recovery point capture time in Unix milliseconds.
    pub captured_at_millis: u64,
    /// Measured source state digest as lowercase hexadecimal.
    pub source_checksum: String,
    /// Measured source row count.
    pub source_row_count: u64,
    /// Measured restored state digest as lowercase hexadecimal.
    pub target_checksum: String,
    /// Measured restored row count.
    pub target_row_count: u64,
    /// Accepted restore fencing epoch.
    pub restore_epoch: u64,
    /// Durable idempotency identifier.
    pub idempotency_key: String,
    /// Owned restore operation identifier.
    pub operation_id: String,
    /// Owned native recovery point identifier.
    pub recovery_point_id: String,
    /// Exact accepted ordered placement inventory.
    pub placement: Vec<String>,
    /// Restore completion time in Unix milliseconds.
    pub completed_at_millis: u64,
}

/// Receipt rejection category; never includes secret key material.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NativeRecoveryReceiptError {
    /// Malformed or inconsistent observed facts or signing identity.
    InvalidBinding,
    /// Malformed, oversized, or unsupported encoding.
    Encoding,
    /// Signature does not verify under the pinned key and domain.
    InvalidSignature,
    /// Receipt differs from the consumer's owned request.
    RequestMismatch,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Envelope {
    key_id: String,
    payload_base64: String,
    signature_base64: String,
}

/// Dedicated explicitly provisioned Ed25519 receipt signer.
pub struct NativeRecoveryReceiptSigner {
    key_id: String,
    key: Ed25519KeyPair,
}

impl NativeRecoveryReceiptSigner {
    /// Loads a provisioned PKCS#8 key without any generated or fallback identity.
    /// # Errors
    /// Rejects invalid key identifiers and invalid or oversized private keys.
    pub fn from_pkcs8(key_id: String, der: &[u8]) -> Result<Self, NativeRecoveryReceiptError> {
        if !bounded_text(&key_id, 128) || der.len() > 16 * 1024 {
            return Err(NativeRecoveryReceiptError::InvalidBinding);
        }
        let key = Ed25519KeyPair::from_pkcs8(der)
            .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        Ok(Self { key_id, key })
    }

    // Only the backend can invoke signing after observing durable restored state.
    pub(crate) fn sign_verified(
        &self,
        facts: &NativeRecoveryReceipt,
    ) -> Result<Vec<u8>, NativeRecoveryReceiptError> {
        facts.validate()?;
        let payload =
            serde_json::to_vec(facts).map_err(|_| NativeRecoveryReceiptError::Encoding)?;
        let signature = self.key.sign(&signature_message(&payload));
        let bytes = serde_json::to_vec(&Envelope {
            key_id: self.key_id.clone(),
            payload_base64: STANDARD.encode(payload),
            signature_base64: STANDARD.encode(signature.as_ref()),
        })
        .map_err(|_| NativeRecoveryReceiptError::Encoding)?;
        if bytes.len() > MAX_NATIVE_RECOVERY_RECEIPT_BYTES {
            return Err(NativeRecoveryReceiptError::Encoding);
        }
        Ok(bytes)
    }
}

impl NativeRecoveryReceipt {
    fn validate(&self) -> Result<(), NativeRecoveryReceiptError> {
        let source = DatabaseRouteScope::try_new(
            &self.tenant_id,
            &self.project_id,
            &self.source_database_id,
        );
        let target = DatabaseRouteScope::try_new(
            &self.tenant_id,
            &self.project_id,
            &self.target_database_id,
        );
        if self.schema_version != 1
            || source.is_err()
            || target.is_err()
            || self.source_database_id == self.target_database_id
            || self.restore_epoch == 0
            || self.captured_at_millis == 0
            || self.completed_at_millis < self.captured_at_millis
            || !hex_digest(&self.source_checksum)
            || self.source_checksum != self.target_checksum
            || self.source_row_count != self.target_row_count
            || [&self.idempotency_key, &self.operation_id, &self.recovery_point_id]
                .iter()
                .any(|id| !bounded_text(id, 256))
            || self.placement.is_empty()
            || self.placement.len() > 20
            || self.placement.iter().any(|region| {
                region.is_empty()
                    || region.len() > 32
                    || !region.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
            })
            || self.placement.iter().collect::<std::collections::BTreeSet<_>>().len()
                != self.placement.len()
        {
            return Err(NativeRecoveryReceiptError::InvalidBinding);
        }
        Ok(())
    }
}

/// Authenticated facts, still requiring comparison against the owned request.
#[derive(Clone, Debug)]
pub struct VerifiedNativeRecoveryReceipt(NativeRecoveryReceipt);

impl VerifiedNativeRecoveryReceipt {
    /// Returns the authenticated backend observations.
    #[must_use]
    pub const fn receipt(&self) -> &NativeRecoveryReceipt {
        &self.0
    }

    /// Compares every signed field against the consumer's owned expected facts.
    /// # Errors
    /// Rejects malformed expected facts and any different scope or operation data.
    pub fn matches_request(
        &self,
        expected: &NativeRecoveryReceipt,
    ) -> Result<(), NativeRecoveryReceiptError> {
        expected.validate()?;
        if &self.0 != expected {
            return Err(NativeRecoveryReceiptError::RequestMismatch);
        }
        Ok(())
    }
}

/// Verifies bounded JSON with an explicitly pinned raw 32-byte Ed25519 public key.
/// The signature covers decoded raw payload bytes, never reserialized JSON.
/// # Errors
/// Rejects unknown fields, bad identities, malformed payloads and bad signatures.
pub fn verify_receipt(
    bytes: &[u8],
    expected_key_id: &str,
    public_key: &[u8],
) -> Result<VerifiedNativeRecoveryReceipt, NativeRecoveryReceiptError> {
    if bytes.len() > MAX_NATIVE_RECOVERY_RECEIPT_BYTES
        || !bounded_text(expected_key_id, 128)
        || public_key.len() != 32
    {
        return Err(NativeRecoveryReceiptError::Encoding);
    }
    let envelope: Envelope =
        serde_json::from_slice(bytes).map_err(|_| NativeRecoveryReceiptError::Encoding)?;
    if envelope.key_id != expected_key_id {
        return Err(NativeRecoveryReceiptError::InvalidSignature);
    }
    let payload = STANDARD
        .decode(&envelope.payload_base64)
        .map_err(|_| NativeRecoveryReceiptError::Encoding)?;
    let signature = STANDARD
        .decode(&envelope.signature_base64)
        .map_err(|_| NativeRecoveryReceiptError::Encoding)?;
    if signature.len() != 64 {
        return Err(NativeRecoveryReceiptError::InvalidSignature);
    }
    UnparsedPublicKey::new(&ED25519, public_key)
        .verify(&signature_message(&payload), &signature)
        .map_err(|_| NativeRecoveryReceiptError::InvalidSignature)?;
    let facts: NativeRecoveryReceipt =
        serde_json::from_slice(&payload).map_err(|_| NativeRecoveryReceiptError::Encoding)?;
    facts.validate()?;
    Ok(VerifiedNativeRecoveryReceipt(facts))
}

fn signature_message(payload: &[u8]) -> Vec<u8> {
    let mut message = Vec::with_capacity(NATIVE_RECOVERY_SIGNATURE_DOMAIN.len() + payload.len());
    message.extend_from_slice(NATIVE_RECOVERY_SIGNATURE_DOMAIN);
    message.extend_from_slice(payload);
    message
}
fn bounded_text(value: &str, limit: usize) -> bool {
    !value.is_empty() && value.len() <= limit && value.bytes().all(|byte| byte.is_ascii_graphic())
}
fn hex_digest(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

#[cfg(test)]
mod tests {
    use super::*;
    use aws_lc_rs::signature::KeyPair;

    fn facts() -> NativeRecoveryReceipt {
        NativeRecoveryReceipt {
            schema_version: 1,
            tenant_id: "01ARZ3NDEKTSV4RRFFQ69G5FAV".into(),
            project_id: "01ARZ3NDEKTSV4RRFFQ69G5FAW".into(),
            source_database_id: "01ARZ3NDEKTSV4RRFFQ69G5FAX".into(),
            target_database_id: "01ARZ3NDEKTSV4RRFFQ69G5FAY".into(),
            source_journal_cut: 42,
            captured_at_millis: 1000,
            source_checksum: "a".repeat(64),
            source_row_count: 12,
            target_checksum: "a".repeat(64),
            target_row_count: 12,
            restore_epoch: 3,
            idempotency_key: "restore-once".into(),
            operation_id: "restore-op".into(),
            recovery_point_id: "snapshot-1".into(),
            placement: vec!["gru".into(), "sfo".into()],
            completed_at_millis: 2000,
        }
    }
    fn signer() -> NativeRecoveryReceiptSigner {
        let der = Ed25519KeyPair::generate_pkcs8(&aws_lc_rs::rand::SystemRandom::new()).unwrap();
        NativeRecoveryReceiptSigner::from_pkcs8("key-1".into(), der.as_ref()).unwrap()
    }
    #[test]
    fn binds_domain_raw_payload_and_pinned_identity() {
        let signer = signer();
        let public = signer.key.public_key();
        let bytes = signer.sign_verified(&facts()).unwrap();
        verify_receipt(&bytes, "key-1", public.as_ref())
            .unwrap()
            .matches_request(&facts())
            .unwrap();
        assert!(verify_receipt(&bytes, "other", public.as_ref()).is_err());
        let mut envelope: Envelope = serde_json::from_slice(&bytes).unwrap();
        let raw = STANDARD.decode(&envelope.payload_base64).unwrap();
        let signature = STANDARD.decode(&envelope.signature_base64).unwrap();
        assert!(
            UnparsedPublicKey::new(&ED25519, public.as_ref()).verify(&raw, &signature).is_err()
        );
        let mut wrong_domain = signature_message(&raw);
        wrong_domain[0] ^= 1;
        assert!(
            UnparsedPublicKey::new(&ED25519, public.as_ref())
                .verify(&wrong_domain, &signature)
                .is_err()
        );
        let changed = String::from_utf8(raw).unwrap().replace("restore-op", "foreign-op");
        envelope.payload_base64 = STANDARD.encode(changed);
        assert!(
            verify_receipt(&serde_json::to_vec(&envelope).unwrap(), "key-1", public.as_ref())
                .is_err()
        );
    }
    #[test]
    fn rejects_foreign_request_bindings() {
        let signer = signer();
        let verified = verify_receipt(
            &signer.sign_verified(&facts()).unwrap(),
            "key-1",
            signer.key.public_key().as_ref(),
        )
        .unwrap();
        for field in 0..5 {
            let mut expected = facts();
            match field {
                0 => expected.target_database_id = "01ARZ3NDEKTSV4RRFFQ69G5FAZ".into(),
                1 => expected.operation_id = "other".into(),
                2 => expected.restore_epoch += 1,
                3 => expected.source_journal_cut += 1,
                _ => expected.source_database_id = "01ARZ3NDEKTSV4RRFFQ69G5FAZ".into(),
            }
            assert_eq!(
                verified.matches_request(&expected),
                Err(NativeRecoveryReceiptError::RequestMismatch)
            );
        }
    }
    #[test]
    fn refuses_inconsistent_facts_and_unbounded_inputs() {
        let signer = signer();
        let mut invalid = facts();
        invalid.target_row_count += 1;
        assert!(signer.sign_verified(&invalid).is_err());
        invalid = facts();
        invalid.target_checksum = "b".repeat(64);
        assert!(signer.sign_verified(&invalid).is_err());
        invalid = facts();
        invalid.placement.push("gru".into());
        assert!(signer.sign_verified(&invalid).is_err());
        assert!(
            verify_receipt(
                &vec![b' '; MAX_NATIVE_RECOVERY_RECEIPT_BYTES + 1],
                "key-1",
                signer.key.public_key().as_ref()
            )
            .is_err()
        );
        assert!(NativeRecoveryReceiptSigner::from_pkcs8("key-1".into(), b"invalid").is_err());
    }
}
