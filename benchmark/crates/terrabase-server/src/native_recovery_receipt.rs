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
    /// SHA-256 identity of the authenticated source manifest.
    pub recovery_point_digest: String,
    /// Exact restored database incarnation, admitted before mutation.
    pub target_incarnation: String,
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
    /// Loads an owner-private, bounded PKCS#8 DER file under the configured identity.
    /// # Errors
    /// Rejects symlinks, public file permissions, invalid keys and missing identities.
    pub fn from_file(
        key_id: String,
        path: &std::path::Path,
    ) -> Result<Self, NativeRecoveryReceiptError> {
        use std::io::Read as _;
        let mut file = crate::runtime::open_readonly(path)
            .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        let metadata = file.metadata().map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        if !metadata.is_file() || metadata.len() == 0 || metadata.len() > 16 * 1024 {
            return Err(NativeRecoveryReceiptError::InvalidBinding);
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            if metadata.permissions().mode() & 0o077 != 0 {
                return Err(NativeRecoveryReceiptError::InvalidBinding);
            }
        }
        let mut bytes = Vec::new();
        file.by_ref()
            .take(16 * 1024 + 1)
            .read_to_end(&mut bytes)
            .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        Self::from_pkcs8(key_id, &bytes)
    }

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

/// Durable immutable signed observations. A pending restore never enters this store.
pub struct NativeRecoveryReceiptStore {
    signer: NativeRecoveryReceiptSigner,
    journal: std::sync::Mutex<rusqlite::Connection>,
}

impl NativeRecoveryReceiptStore {
    /// Opens the owner-private receipt journal with fully durable commits.
    /// # Errors
    /// Rejects unavailable journals and malformed signing material.
    pub fn open(
        path: &std::path::Path,
        signer: NativeRecoveryReceiptSigner,
    ) -> Result<Self, NativeRecoveryReceiptError> {
        crate::export_api::ensure_no_symlink_components(path)
            .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        let journal = rusqlite::Connection::open(path)
            .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600))
                .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        }
        journal.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS receipts (tenant TEXT NOT NULL, project TEXT NOT NULL, operation TEXT NOT NULL, payload BLOB NOT NULL, envelope BLOB NOT NULL, PRIMARY KEY(tenant, project, operation));")
            .map_err(|_| NativeRecoveryReceiptError::InvalidBinding)?;
        Ok(Self { signer, journal: std::sync::Mutex::new(journal) })
    }

    /// Persists a signature only for Complete state and independently authenticated source facts.
    pub(crate) fn completed(
        &self,
        operation: &crate::native_recovery::NativeRestoreOperation,
        source: &crate::native_recovery::RecoveryPointFacts,
    ) -> Result<Vec<u8>, NativeRecoveryReceiptError> {
        use crate::native_recovery::NativeRestorePhase;
        use rusqlite::OptionalExtension as _;
        let invalid = NativeRecoveryReceiptError::InvalidBinding;
        if operation.phase != NativeRestorePhase::Complete {
            return Err(invalid);
        }
        let restored = operation.receipt.as_ref().ok_or(invalid)?;
        let request = &operation.request;
        let (tenant, project) = request.target_owner.split_once('/').ok_or(invalid)?;
        if request.source_owner != request.target_owner
            || source.source_database_id != request.source_database_id
            || source.recovery_point_id != request.recovery_point_id
            || restored.selected_identity != request.recovery_point_digest
            || source.journal_cut != restored.journal_cut
            || request.journal_cut.parse::<u64>().ok() != Some(source.journal_cut)
            || source.placement != request.placement
            || source.checksum != restored.logical_content_checksum
            || source.row_count != restored.restored_documents
        {
            return Err(invalid);
        }
        let mut facts = NativeRecoveryReceipt {
            schema_version: 1,
            tenant_id: tenant.into(),
            project_id: project.into(),
            source_database_id: request.source_database_id.clone(),
            target_database_id: request.target_database_id.clone(),
            target_incarnation: request.target_database_id.clone(),
            source_journal_cut: source.journal_cut,
            captured_at_millis: source.captured_at_millis,
            source_checksum: source.checksum.clone(),
            source_row_count: source.row_count,
            target_checksum: restored.logical_content_checksum.clone(),
            target_row_count: restored.restored_documents,
            restore_epoch: request.restore_epoch,
            idempotency_key: request.operation_id.clone(),
            operation_id: request.operation_id.clone(),
            recovery_point_id: request.recovery_point_id.clone(),
            recovery_point_digest: request.recovery_point_digest.clone(),
            placement: request.placement.clone(),
            completed_at_millis: source.captured_at_millis,
        };
        facts.validate()?;
        let mut journal = self.journal.lock().map_err(|_| invalid)?;
        let transaction = journal
            .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)
            .map_err(|_| invalid)?;
        let stored: Option<(Vec<u8>, Vec<u8>)> = transaction.query_row(
            "SELECT payload, envelope FROM receipts WHERE tenant=?1 AND project=?2 AND operation=?3",
            rusqlite::params![tenant, project, request.operation_id], |row| Ok((row.get(0)?, row.get(1)?))
        ).optional().map_err(|_| invalid)?;
        if let Some((payload, envelope)) = stored {
            let previous: NativeRecoveryReceipt =
                serde_json::from_slice(&payload).map_err(|_| invalid)?;
            facts.completed_at_millis = previous.completed_at_millis;
            if facts != previous {
                return Err(NativeRecoveryReceiptError::RequestMismatch);
            }
            use aws_lc_rs::signature::KeyPair as _;
            verify_receipt(&envelope, &self.signer.key_id, self.signer.key.public_key().as_ref())?
                .matches_request(&facts)?;
            return Ok(envelope);
        }
        facts.completed_at_millis = u64::try_from(
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(|_| invalid)?
                .as_millis(),
        )
        .map_err(|_| invalid)?;
        let envelope = self.signer.sign_verified(&facts)?;
        let payload = serde_json::to_vec(&facts).map_err(|_| invalid)?;
        transaction.execute("INSERT INTO receipts (tenant,project,operation,payload,envelope) VALUES (?1,?2,?3,?4,?5)", rusqlite::params![tenant, project, request.operation_id, payload, envelope]).map_err(|_| invalid)?;
        transaction.commit().map_err(|_| invalid)?;
        Ok(envelope)
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
            || !hex_digest(&self.recovery_point_digest)
            || self.target_incarnation != self.target_database_id
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
            recovery_point_digest: "b".repeat(64),
            target_incarnation: "01ARZ3NDEKTSV4RRFFQ69G5FAY".into(),
            placement: vec!["gru".into(), "sfo".into()],
            completed_at_millis: 2000,
        }
    }
    fn signer() -> NativeRecoveryReceiptSigner {
        let der = Ed25519KeyPair::generate_pkcs8(&aws_lc_rs::rand::SystemRandom::new()).unwrap();
        NativeRecoveryReceiptSigner::from_pkcs8("key-1".into(), der.as_ref()).unwrap()
    }
    #[test]
    fn durable_completed_observation_rejects_pending_rebinding_and_replays_exactly() {
        use crate::native_recovery::*;
        let directory = std::env::temp_dir().join(format!(
            "native-receipt-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir(&directory).unwrap();
        let store =
            NativeRecoveryReceiptStore::open(&directory.join("receipts.sqlite"), signer()).unwrap();
        let expected = facts();
        let mut operation = NativeRestoreOperation {
            request: NativeRestoreRequest {
                operation_id: expected.operation_id.clone(),
                source_owner: format!("{}/{}", expected.tenant_id, expected.project_id),
                source_database_id: expected.source_database_id.clone(),
                recovery_point_id: expected.recovery_point_id.clone(),
                recovery_point_digest: expected.recovery_point_digest.clone(),
                target_owner: format!("{}/{}", expected.tenant_id, expected.project_id),
                target_database_id: expected.target_database_id.clone(),
                journal_cut: expected.source_journal_cut.to_string(),
                restore_epoch: expected.restore_epoch,
                authority_epoch: 7,
                authority_lease: "lease".into(),
                accepted_proof_digest: "b".repeat(64),
                provider: "oci".into(),
                selection: NativeRestoreSelection::PointId(expected.recovery_point_id.clone()),
                residency: "GRU".into(),
                placement: expected.placement.clone(),
            },
            phase: NativeRestorePhase::Accepted,
            receipt: Some(NativeRestoreReceipt {
                selected_identity: expected.recovery_point_digest.clone(),
                logical_content_checksum: expected.target_checksum.clone(),
                restored_documents: expected.target_row_count,
                journal_cut: expected.source_journal_cut,
            }),
            error: None,
        };
        let source = RecoveryPointFacts {
            recovery_point_id: expected.recovery_point_id.clone(),
            source_database_id: expected.source_database_id.clone(),
            journal_cut: expected.source_journal_cut,
            captured_at_millis: expected.captured_at_millis,
            checksum: expected.source_checksum.clone(),
            row_count: expected.source_row_count,
            placement: expected.placement.clone(),
            retained_until_millis: 100_000,
        };
        assert!(store.completed(&operation, &source).is_err());
        operation.phase = NativeRestorePhase::Complete;
        let first = store.completed(&operation, &source).unwrap();
        assert_eq!(store.completed(&operation, &source).unwrap(), first);
        let NativeRecoveryReceiptStore { signer, journal } = store;
        drop(journal);
        let store =
            NativeRecoveryReceiptStore::open(&directory.join("receipts.sqlite"), signer).unwrap();
        assert_eq!(store.completed(&operation, &source).unwrap(), first);
        operation.request.restore_epoch += 1;
        assert!(store.completed(&operation, &source).is_err());
        operation.request.restore_epoch -= 1;
        operation.request.target_owner =
            "01ARZ3NDEKTSV4RRFFQ69G5FAZ/01ARZ3NDEKTSV4RRFFQ69G5FAW".into();
        assert!(store.completed(&operation, &source).is_err());
        operation.request.target_owner = operation.request.source_owner.clone();
        let mut wrong_target = operation.clone();
        wrong_target.receipt.as_mut().unwrap().logical_content_checksum = "e".repeat(64);
        assert!(store.completed(&wrong_target, &source).is_err());
        wrong_target = operation.clone();
        wrong_target.receipt.as_mut().unwrap().restored_documents += 1;
        assert!(store.completed(&wrong_target, &source).is_err());
        let mut wrong_source = source.clone();
        wrong_source.checksum = "f".repeat(64);
        assert!(store.completed(&operation, &wrong_source).is_err());
        store
            .journal
            .lock()
            .unwrap()
            .execute("UPDATE receipts SET envelope=?1", [b"tampered".as_slice()])
            .unwrap();
        assert!(store.completed(&operation, &source).is_err());
        drop(store);
        std::fs::remove_dir_all(directory).unwrap();
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
                0 => {
                    expected.target_database_id = "01ARZ3NDEKTSV4RRFFQ69G5FAZ".into();
                    expected.target_incarnation = expected.target_database_id.clone();
                }
                1 => expected.operation_id = "foreign-operation".into(),
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
