//! Member receipts over measured replication boundaries.
//!
//! The signature covers the exact compact JSON bytes after a versioned domain.
//! Configuration, connection flags, and an accepted placement are never evidence
//! of a shared snapshot or a reconciled causal cut.

use std::collections::{BTreeMap, BTreeSet};

use aws_lc_rs::signature::Ed25519KeyPair;
use base64::{Engine as _, engine::general_purpose::STANDARD};
use serde::{Deserialize, Serialize};
use terrabase_cluster::production::DatabaseRouteScope;

/// Domain separating database readiness from fencing and backup receipts.
pub const READINESS_SIGNATURE_DOMAIN: &[u8] = b"terrabase-database-readiness-v1\0";
/// Maximum opaque receipt size accepted by the control transport.
pub const MAX_READINESS_RECEIPT_BYTES: usize = 256 * 1024;

/// Immutable accepted admission and placement against which evidence is checked.
#[derive(Clone, Debug)]
pub struct ReadinessBinding {
    /// Workload environment from authenticated member identity.
    pub environment: String,
    /// Exact admitted database identity.
    pub scope: DatabaseRouteScope,
    /// Database encryption key version from the durable admission manifest.
    pub key_version: String,
    /// Authority's database placement operation identifier.
    pub operation_id: String,
    /// Authenticated local workload member identity.
    pub member_identity: String,
    /// Local selected Region.
    pub region: String,
    /// Accepted replica membership epoch.
    pub membership_epoch: u64,
    /// Accepted placement generation.
    pub generation: u64,
    /// SHA-256 of canonical deterministic PlacementProposal protobuf bytes.
    pub placement_digest: String,
    /// Complete selected Region inventory from that accepted placement.
    pub selected_regions: BTreeSet<String>,
}

/// Reasons the member cannot issue a readiness receipt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReadinessProofError {
    /// Invalid or mismatched accepted admission, challenge, or signing identity.
    InvalidBinding,
    /// No authenticated matching physical snapshot and durable WAL boundary.
    PhysicalBoundaryUnavailable,
    /// No verified equal state root at the same contiguous causal cut.
    CausalBoundaryUnavailable,
    /// Assignment revoked, runtime stopped, or evidence lock unavailable.
    Fenced,
    /// Canonical serialization, signing, or size validation failed.
    Encoding,
}

/// Dedicated workload receipt key; secret bytes never appear in diagnostics.
pub struct MemberReceiptSigner {
    key_id: String,
    key: Ed25519KeyPair,
}

impl MemberReceiptSigner {
    /// Loads a dedicated member receipt key supplied by workload provisioning.
    /// The matching public key must be pinned by the readiness consumer.
    ///
    /// # Errors
    /// Rejects malformed PKCS#8 Ed25519 keys or unbounded key identifiers.
    pub fn from_pkcs8(key_id: String, der: &[u8]) -> Result<Self, ReadinessProofError> {
        if !bounded_text(&key_id, 128) || der.len() > 16 * 1024 {
            return Err(ReadinessProofError::InvalidBinding);
        }
        let key =
            Ed25519KeyPair::from_pkcs8(der).map_err(|_| ReadinessProofError::InvalidBinding)?;
        Ok(Self { key_id, key })
    }

    fn sign(&self, payload: &ReadinessPayload) -> Result<Vec<u8>, ReadinessProofError> {
        let raw = serde_json::to_vec(payload).map_err(|_| ReadinessProofError::Encoding)?;
        let mut message = Vec::with_capacity(READINESS_SIGNATURE_DOMAIN.len() + raw.len());
        message.extend_from_slice(READINESS_SIGNATURE_DOMAIN);
        message.extend_from_slice(&raw);
        let signature = self.key.sign(&message);
        let envelope = SignedReadinessReceipt {
            key_id: &self.key_id,
            payload: STANDARD.encode(raw),
            signature: STANDARD.encode(signature.as_ref()),
        };
        let bytes = serde_json::to_vec(&envelope).map_err(|_| ReadinessProofError::Encoding)?;
        if bytes.len() > MAX_READINESS_RECEIPT_BYTES {
            return Err(ReadinessProofError::Encoding);
        }
        Ok(bytes)
    }
}

#[derive(Serialize)]
struct SignedReadinessReceipt<'a> {
    key_id: &'a str,
    payload: String,
    signature: String,
}

// Field order is the v1 canonical producer order. The verifier verifies raw
// bytes first, then strictly decodes: re-encoding JSON before verification is
// forbidden. Frontier keys use BTreeMap's byte-lexical Region ordering.
#[derive(Serialize)]
struct ReadinessPayload {
    schema_version: u32,
    environment: String,
    tenant_id: String,
    project_id: String,
    database_id: String,
    key_version: String,
    operation_id: String,
    member_identity: String,
    region: String,
    membership_epoch: u64,
    generation: u64,
    placement_digest: String,
    physical: PhysicalBoundary,
    causal: CausalBoundary,
    observed_at_unix_ms: u64,
    nonce: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
struct PhysicalBoundary {
    snapshot_digest: String,
    wal_checkpoint_digest: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct CausalBoundary {
    cut_digest: String,
    frontier: BTreeMap<String, u64>,
    root_digest: String,
}

impl ReadinessBinding {
    fn validate(&self) -> Result<(), ReadinessProofError> {
        if !bounded_text(&self.environment, 64)
            || !bounded_text(&self.operation_id, 256)
            || !bounded_text(&self.member_identity, 256)
            || self.membership_epoch == 0
            || self.generation == 0
            || !hex_digest(&self.placement_digest)
            || self.selected_regions.is_empty()
            || self.selected_regions.len() > 20
            || !self.selected_regions.contains(&self.region)
            || self.selected_regions.iter().any(|region| {
                region.is_empty()
                    || region.len() > 32
                    || !region.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
            })
            || terrabase::encryption::DatabaseKeyVersion::from_text(&self.key_version).is_err()
        {
            return Err(ReadinessProofError::InvalidBinding);
        }
        Ok(())
    }
}

fn bounded_text(value: &str, limit: usize) -> bool {
    !value.is_empty() && value.len() <= limit && value.bytes().all(|byte| byte.is_ascii_graphic())
}
fn hex_digest(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}
fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    bytes.iter().fold(String::with_capacity(bytes.len() * 2), |mut result, byte| {
        let _ = write!(result, "{byte:02x}");
        result
    })
}


/// Physical-lane challenge carrying the writer's measured logical cut.
/// A decoded frame is untrusted until checked against the authenticated writer
/// and the follower's actual durable physical checkpoint.
#[derive(Clone)]
pub struct ReadinessCutFrame {
    scope: DatabaseRouteScope,
    body: ReadinessCutBody,
    source_verified: bool,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ReadinessCutBody {
    generation: u64,
    epoch: u64,
    snapshot_digest: [u8; 32],
    checkpoint_digest: [u8; 32],
    sequence: u64,
    causal: CausalBoundary,
}

/// Logical cut bound to an authenticated byte-identical RF2 checkpoint.
/// Construction requires a sealed driver proof; callers cannot populate fields.
#[derive(Clone, Debug)]
pub struct VerifiedReadinessCut {
    scope: DatabaseRouteScope,
    generation: u64,
    epoch: u64,
    physical: PhysicalBoundary,
    causal: CausalBoundary,
    sequence: u64,
    observed_at_unix_ms: u64,
}

impl ReadinessCutFrame {
    /// Builds a physical challenge from an actual correlated repair cut.
    ///
    /// # Errors
    /// Refuses a physical prefix that does not cover the measured local cut.
    pub fn from_verified(
        cut: &terrabase_tbep::production_repair::VerifiedRepairCut,
        physical: &terrabase_cluster::wal_driver::WalPhysicalBoundary,
    ) -> Result<Self, ReadinessProofError> {
        if cut.scope() != physical.scope()
            || cut.replica_epoch() != physical.epoch()
            || cut.local_sequence() > physical.sequence()
        {
            return Err(ReadinessProofError::PhysicalBoundaryUnavailable);
        }
        Ok(Self {
            scope: cut.scope(),
            body: ReadinessCutBody {
                generation: cut.placement_generation(),
                epoch: cut.replica_epoch(),
                snapshot_digest: physical.snapshot_digest(),
                checkpoint_digest: physical.checkpoint_digest(),
                sequence: cut.local_sequence(),
                causal: CausalBoundary {
                    cut_digest: hex(&cut.cut_digest()),
                    frontier: cut.frontier().clone(),
                    root_digest: hex(&cut.root_digest()),
                },
            },
            source_verified: true,
        })
    }

    /// Exact database routing tuple used by the physical lane dispatcher.
    #[must_use]
    pub const fn scope(&self) -> DatabaseRouteScope { self.scope }
    /// Accepted placement generation bound to the logical cut.
    #[must_use]
    pub const fn generation(&self) -> u64 { self.body.generation }
    /// Accepted replica epoch bound to the physical checkpoint.
    #[must_use]
    pub const fn epoch(&self) -> u64 { self.body.epoch }
    /// Authenticated installed base snapshot content digest.
    #[must_use]
    pub const fn snapshot_digest(&self) -> [u8; 32] { self.body.snapshot_digest }
    /// Exact WAL checkpoint to verify against the follower's synced state.
    #[must_use]
    pub const fn checkpoint_digest(&self) -> [u8; 32] { self.body.checkpoint_digest }
    /// Writer's SQLite sequence containing the logical cut.
    #[must_use]
    pub const fn sequence(&self) -> u64 { self.body.sequence }

    /// Encodes one bounded, scoped physical cut challenge.
    /// # Errors
    /// Refuses invalid fields or a frame exceeding 64 KiB.
    pub fn encode(&self) -> Result<Vec<u8>, ReadinessProofError> {
        self.validate()?;
        let mut bytes = b"TBRCUT\0\x01".to_vec();
        for id in [self.scope.tenant().to_text(), self.scope.project().to_text(), self.scope.database().to_text()] {
            bytes.extend_from_slice(id.as_bytes());
        }
        bytes.extend(serde_json::to_vec(&self.body).map_err(|_| ReadinessProofError::Encoding)?);
        if bytes.len() > 64 * 1024 { return Err(ReadinessProofError::Encoding); }
        Ok(bytes)
    }

    /// Decodes untrusted bytes without granting proof authority.
    /// # Errors
    /// Rejects unknown versions, noncanonical JSON, excess size and invalid scope.
    pub fn decode(bytes: &[u8]) -> Result<Self, ReadinessProofError> {
        if bytes.len() < 87 || bytes.len() > 64 * 1024 || &bytes[..8] != b"TBRCUT\0\x01" {
            return Err(ReadinessProofError::Encoding);
        }
        let id = |start| std::str::from_utf8(&bytes[start..start + 26]).map_err(|_| ReadinessProofError::InvalidBinding);
        let scope = DatabaseRouteScope::try_new(id(8)?, id(34)?, id(60)?)
            .map_err(|_| ReadinessProofError::InvalidBinding)?;
        let body = serde_json::from_slice(&bytes[86..]).map_err(|_| ReadinessProofError::Encoding)?;
        let frame = Self { scope, body, source_verified: false };
        if frame.encode()? != bytes { return Err(ReadinessProofError::Encoding); }
        Ok(frame)
    }

    /// Digest bound by the follower's sealed physical verification result.
    /// # Errors
    /// Refuses invalid frame fields.
    pub fn digest(&self) -> Result<[u8; 32], ReadinessProofError> {
        let bytes = self.encode()?;
        let mut scoped = b"terrabase-physical-readiness-cut-v1\0".to_vec();
        scoped.extend(bytes);
        let digest = aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, &scoped);
        digest.as_ref().try_into().map_err(|_| ReadinessProofError::Encoding)
    }

    /// Binds a locally measured logical cut to the writer's RF2 receipt evidence.
    /// # Errors
    /// Refuses decoded frames and a different or insufficient physical boundary.
    pub fn authorize_writer(
        &self,
        physical: &terrabase_cluster::wal_driver::WalPhysicalBoundary,
    ) -> Result<VerifiedReadinessCut, ReadinessProofError> {
        if !self.source_verified
            || physical.scope() != self.scope
            || physical.epoch() != self.body.epoch
            || physical.snapshot_digest() != self.body.snapshot_digest
            || physical.checkpoint_digest() != self.body.checkpoint_digest
            || physical.sequence() < self.body.sequence
        {
            return Err(ReadinessProofError::PhysicalBoundaryUnavailable);
        }
        self.verified()
    }

    /// Binds the remote writer's logical cut to this follower's durable bytes.
    /// # Errors
    /// Refuses proofs for another scope, checkpoint, or logical challenge.
    pub fn authenticate(
        &self,
        proof: &terrabase_cluster::wal_driver::WalVerifiedCut,
    ) -> Result<VerifiedReadinessCut, ReadinessProofError> {
        if proof.scope() != self.scope
            || proof.epoch() != self.body.epoch
            || proof.snapshot_digest() != self.body.snapshot_digest
            || proof.checkpoint_digest() != self.body.checkpoint_digest
            || proof.sequence() != self.body.sequence
            || proof.cut_digest() != self.digest()?
        {
            return Err(ReadinessProofError::PhysicalBoundaryUnavailable);
        }
        self.verified()
    }

    fn validate(&self) -> Result<(), ReadinessProofError> {
        if self.body.generation == 0 || self.body.epoch == 0
            || !hex_digest(&self.body.causal.cut_digest)
            || !hex_digest(&self.body.causal.root_digest)
            || self.body.causal.frontier.len() > 20
            || self.body.causal.frontier.keys().any(|origin| {
                origin.is_empty() || origin.len() > 32
                    || !origin.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
            })
        {
            return Err(ReadinessProofError::InvalidBinding);
        }
        Ok(())
    }

    fn verified(&self) -> Result<VerifiedReadinessCut, ReadinessProofError> {
        self.validate()?;
        Ok(VerifiedReadinessCut {
            scope: self.scope,
            generation: self.body.generation,
            epoch: self.body.epoch,
            physical: PhysicalBoundary {
                snapshot_digest: hex(&self.body.snapshot_digest),
                wal_checkpoint_digest: hex(&self.body.checkpoint_digest),
            },
            causal: self.body.causal.clone(),
            sequence: self.body.sequence,
            observed_at_unix_ms: unix_millis()?,
        })
    }
}

fn unix_millis() -> Result<u64, ReadinessProofError> {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .ok()
        .and_then(|duration| u64::try_from(duration.as_millis()).ok())
        .filter(|millis| *millis > 0 && *millis <= i64::MAX as u64)
        .ok_or(ReadinessProofError::Encoding)
}

/// Issues a challenge-bound receipt from the live driver evidence ledger.
///
/// The caller must supply the immutable binding from durable admission and the
/// accepted placement, and fence this progress ledger before revoking placement.
/// The evidence lock remains held through signing so a disconnect cannot replace
/// the measured peer assignment midway through issuance.
///
/// # Errors
/// Refuses configured-only progress, missing installation or cut evidence,
/// mismatched peer placement, an expired assignment, and malformed challenges.
pub fn issue_readiness(
    binding: &ReadinessBinding,
    challenge_nonce: &[u8],
    signer: &MemberReceiptSigner,
    progress: &crate::replication_runtime::ReplicationProgress,
    observations: &crate::replication_peers::PeerObservations,
) -> Result<Vec<u8>, ReadinessProofError> {
    binding.validate()?;
    if challenge_nonce.len() != 32 || challenge_nonce.iter().all(|byte| *byte == 0) {
        return Err(ReadinessProofError::InvalidBinding);
    }
    progress
        .with_active_boundary(binding.scope, |boundary| {
            let observed = observations.lock().map_err(|_| ReadinessProofError::Fenced)?;
            if !boundary.regional_observed {
                return Err(ReadinessProofError::PhysicalBoundaryUnavailable);
            }
            let mut physical = None;
            let mut physical_sequence = 0;
            let mut causal = None;
            let mut compared_regions = BTreeSet::new();
            for ((peer, scope), facts) in observed.iter() {
                if *scope != binding.scope || !facts.connected {
                    continue;
                }
                if !binding.selected_regions.contains(&facts.region) {
                    return Err(ReadinessProofError::InvalidBinding);
                }
                if let Some(proof) = &facts.physical {
                    if facts.region != binding.region
                        || proof.scope() != binding.scope
                        || proof.epoch() != binding.membership_epoch
                    {
                        return Err(ReadinessProofError::InvalidBinding);
                    }
                    let candidate = PhysicalBoundary {
                        snapshot_digest: hex(&proof.snapshot_digest()),
                        wal_checkpoint_digest: hex(&proof.checkpoint_digest()),
                    };
                    if physical.as_ref().is_some_and(|old| old != &candidate) {
                        return Err(ReadinessProofError::PhysicalBoundaryUnavailable);
                    }
                    physical = Some(candidate);
                    physical_sequence = proof.sequence();
                }
                if let Some(proof) = &facts.repair {
                    if proof.scope() != binding.scope
                        || proof.replica_epoch() != binding.membership_epoch
                        || proof.placement_generation() != binding.generation
                        || proof.peer() != peer
                        || proof.frontier().keys().any(|origin| !binding.selected_regions.contains(origin))
                        || proof.local_sequence() > boundary.regional_sequence
                    {
                        return Err(ReadinessProofError::CausalBoundaryUnavailable);
                    }
                    let candidate = CausalBoundary {
                        cut_digest: hex(&proof.cut_digest()),
                        frontier: proof.frontier().clone(),
                        root_digest: hex(&proof.root_digest()),
                    };
                    if causal.as_ref().is_some_and(|old| old != &candidate) {
                        return Err(ReadinessProofError::CausalBoundaryUnavailable);
                    }
                    causal = Some(candidate);
                    compared_regions.insert(facts.region.clone());
                }
            }
            let physical = physical.ok_or(ReadinessProofError::PhysicalBoundaryUnavailable)?;
            let causal = causal.ok_or(ReadinessProofError::CausalBoundaryUnavailable)?;
            if physical_sequence < boundary.regional_sequence {
                return Err(ReadinessProofError::PhysicalBoundaryUnavailable);
            }
            // A local physical pair proves its own Region. Every other selected
            // Region must have compared this same frozen causal cut.
            if binding.selected_regions.iter().any(|region| {
                region != &binding.region && !compared_regions.contains(region)
            }) {
                return Err(ReadinessProofError::CausalBoundaryUnavailable);
            }
            let observed_at_unix_ms = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .ok()
                .and_then(|duration| u64::try_from(duration.as_millis()).ok())
                .filter(|millis| *millis > 0 && *millis <= i64::MAX as u64)
                .ok_or(ReadinessProofError::Encoding)?;
            signer.sign(&ReadinessPayload {
                schema_version: 1,
                environment: binding.environment.clone(),
                tenant_id: binding.scope.tenant().to_text(),
                project_id: binding.scope.project().to_text(),
                database_id: binding.scope.database().to_text(),
                key_version: binding.key_version.clone(),
                operation_id: binding.operation_id.clone(),
                member_identity: binding.member_identity.clone(),
                region: binding.region.clone(),
                membership_epoch: binding.membership_epoch,
                generation: binding.generation,
                placement_digest: binding.placement_digest.clone(),
                physical,
                causal,
                observed_at_unix_ms,
                nonce: hex(challenge_nonce),
            })
        })
        .map_err(|_| ReadinessProofError::Fenced)?
}

#[cfg(test)]
mod tests {
    use super::*;
    use aws_lc_rs::signature::{ED25519, KeyPair, UnparsedPublicKey};

    fn payload() -> ReadinessPayload {
        ReadinessPayload {
            schema_version: 1,
            environment: "production".into(),
            tenant_id: "01ARZ3NDEKTSV4RRFFQ69G5FAV".into(),
            project_id: "01ARZ3NDEKTSV4RRFFQ69G5FAW".into(),
            database_id: "01ARZ3NDEKTSV4RRFFQ69G5FAX".into(),
            key_version: "01ARZ3NDEKTSV4RRFFQ69G5FAY".into(),
            operation_id: "database-create-test".into(),
            member_identity: "member-a".into(),
            region: "gru".into(),
            membership_epoch: 2,
            generation: 3,
            placement_digest: hex(&[1; 32]),
            physical: PhysicalBoundary {
                snapshot_digest: hex(&[2; 32]),
                wal_checkpoint_digest: hex(&[3; 32]),
            },
            causal: CausalBoundary {
                cut_digest: hex(&[4; 32]),
                frontier: BTreeMap::from([("sfo".into(), 5), ("gru".into(), 7)]),
                root_digest: hex(&[5; 32]),
            },
            observed_at_unix_ms: 1_780_000_000_000,
            nonce: hex(&[6; 32]),
        }
    }

    #[test]
    fn receipt_signature_binds_raw_json_domain_and_challenge() {
        let der = Ed25519KeyPair::generate_pkcs8(&aws_lc_rs::rand::SystemRandom::new()).unwrap();
        let signer = MemberReceiptSigner::from_pkcs8("member-key-v1".into(), der.as_ref()).unwrap();
        let public = signer.key.public_key().as_ref().to_vec();
        let encoded = signer.sign(&payload()).unwrap();
        let envelope: serde_json::Value = serde_json::from_slice(&encoded).unwrap();
        let raw = STANDARD.decode(envelope["payload"].as_str().unwrap()).unwrap();
        let signature = STANDARD.decode(envelope["signature"].as_str().unwrap()).unwrap();
        let mut signed = READINESS_SIGNATURE_DOMAIN.to_vec();
        signed.extend_from_slice(&raw);
        let verifier = UnparsedPublicKey::new(&ED25519, public);
        verifier.verify(&signed, &signature).unwrap();
        assert!(verifier.verify(&raw, &signature).is_err());
        signed[0] ^= 1;
        assert!(verifier.verify(&signed, &signature).is_err());
        let json = String::from_utf8(raw).unwrap();
        assert!(json.contains("\"frontier\":{\"gru\":7,\"sfo\":5}"));
        let changed = json.replace(&hex(&[6; 32]), &hex(&[7; 32]));
        let mut replay = READINESS_SIGNATURE_DOMAIN.to_vec();
        replay.extend_from_slice(changed.as_bytes());
        assert!(verifier.verify(&replay, &signature).is_err());
    }

    #[test]
    fn signing_identity_and_digest_encoding_are_bounded() {
        assert!(!hex_digest(&"A".repeat(64)));
        assert!(!hex_digest(&"0".repeat(63)));
        assert!(hex_digest(&hex(&[255; 32])));
        assert!(MemberReceiptSigner::from_pkcs8("key".into(), b"invalid").is_err());
        assert!(MemberReceiptSigner::from_pkcs8(" ".into(), b"invalid").is_err());
    }
}
