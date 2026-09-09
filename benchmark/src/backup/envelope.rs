//! Authenticated multi-participant backup envelopes.
//!
//! A database with admitted shard participants cannot publish independent
//! recovery points and call them one point.  This module binds the logical
//! database incarnation, route generation, and
//! ordered per-participant manifest identities into one digest.  Callers must
//! validate every participant before exposing the envelope as recoverable.

use super::{BackupError, BackupManifest};
use aws_lc_rs::digest::{SHA256, digest};
use serde::{Deserialize, Serialize};
use std::path::Path;

const MAX_PARTICIPANTS: usize = 64;
const MAX_ENVELOPE_BYTES: usize = 256 * 1024;
const HEADER: &str = "terrabase-backup-envelope 1";

/// One participant's immutable recovery point inside an envelope.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupEnvelopeParticipant {
    /// Stable physical participant identity, ordered lexicographically.
    pub participant_id: String,
    /// Physical database identity that owns the participant file.
    pub database_id: String,
    /// Logical database incarnation shared by all participants.
    pub database_incarnation: u128,
    /// Route generation at which the participant was captured.
    pub route_generation: u64,
    /// Local journal boundary captured for this participant.
    pub boundary: u64,
    /// Recovery-point manifest bound to this participant's local boundary.
    pub manifest: BackupManifest,
}

/// A complete multi-participant recovery point envelope.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupEnvelope {
    database_id: String,
    database_incarnation: u128,
    route_generation: u64,
    participants: Vec<BackupEnvelopeParticipant>,
    digest: String,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct EnvelopeWire {
    header: String,
    database_id: String,
    database_incarnation: String,
    route_generation: u64,
    participants: Vec<ParticipantWire>,
    digest: String,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ParticipantWire {
    participant_id: String,
    database_id: String,
    database_incarnation: String,
    route_generation: u64,
    boundary: u64,
    manifest: String,
}

impl BackupEnvelope {
    /// Assembles and validates an envelope from ordered participant manifests.
    pub fn assemble(
        database_id: String,
        database_incarnation: u128,
        route_generation: u64,
        mut participants: Vec<BackupEnvelopeParticipant>,
    ) -> Result<Self, BackupError> {
        if database_id.is_empty()
            || database_incarnation == 0
            || route_generation == u64::MAX
            || participants.is_empty()
            || participants.len() > MAX_PARTICIPANTS
        {
            return Err(BackupError::ManifestInvalid);
        }
        participants.sort_by(|left, right| left.participant_id.cmp(&right.participant_id));
        if participants.windows(2).any(|pair| pair[0].participant_id == pair[1].participant_id) {
            return Err(BackupError::ManifestInvalid);
        }
        for participant in &participants {
            if participant.participant_id.is_empty()
                || participant.database_id.is_empty()
                || participant.database_incarnation != database_incarnation
                || participant.route_generation != route_generation
                || participant.manifest.database_id() != participant.database_id
                || participant.manifest.boundary_sequence().get() != participant.boundary
            {
                return Err(BackupError::ManifestInvalid);
            }
        }
        let mut envelope = Self {
            database_id,
            database_incarnation,
            route_generation,
            participants,
            digest: String::new(),
        };
        envelope.digest = envelope.compute_digest()?;
        Ok(envelope)
    }

    /// Returns the logical database identity bound by this envelope.
    #[must_use]
    pub fn database_id(&self) -> &str {
        &self.database_id
    }

    /// Returns the durable logical database incarnation.
    #[must_use]
    pub const fn database_incarnation(&self) -> u128 {
        self.database_incarnation
    }

    /// Returns the route generation captured by all participants.
    #[must_use]
    pub const fn route_generation(&self) -> u64 {
        self.route_generation
    }

    /// Returns participants in canonical identity order.
    #[must_use]
    pub fn participants(&self) -> &[BackupEnvelopeParticipant] {
        &self.participants
    }

    /// Returns the digest over all identity and manifest facts.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    /// Returns the latest captured clock across all physical participants.
    /// An envelope is the logical recovery point, so callers must not derive
    /// this fact from whichever participant happens to sort first.
    #[must_use]
    pub fn captured_unix_millis(&self) -> u64 {
        self.participants
            .iter()
            .map(|participant| participant.manifest.captured_unix_millis())
            .max()
            .unwrap_or_default()
    }

    /// Returns the earliest retention deadline shared by the participants.
    #[must_use]
    pub fn retain_until_unix_millis(&self) -> u64 {
        self.participants
            .iter()
            .map(|participant| participant.manifest.retain_until_unix_millis())
            .min()
            .unwrap_or_default()
    }

    /// Returns a deterministic logical checksum for the complete envelope.
    /// Participant checksums are length framed and canonicalized by the
    /// envelope's participant order before hashing.
    #[must_use]
    pub fn logical_checksum(&self) -> String {
        let mut hasher = blake3::Hasher::new();
        hasher.update(b"terrabase-backup-envelope-logical-v1\0");
        for participant in &self.participants {
            hasher.update(&(participant.participant_id.len() as u64).to_be_bytes());
            hasher.update(participant.participant_id.as_bytes());
            let checksum = participant.manifest.logical_checksum();
            hasher.update(&(checksum.len() as u64).to_be_bytes());
            hasher.update(checksum.as_bytes());
        }
        hasher.finalize().to_hex().to_string()
    }

    /// Returns the sum of the logical records represented by all participants.
    #[must_use]
    pub fn logical_documents(&self) -> u64 {
        self.participants
            .iter()
            .try_fold(0_u64, |total, participant| {
                total.checked_add(participant.manifest.logical_documents())
            })
            .unwrap_or(u64::MAX)
    }

    /// Encodes the envelope with its digest and strict framing.
    pub fn encode(&self) -> Result<Vec<u8>, BackupError> {
        let wire = self.wire()?;
        let bytes = serde_json::to_vec(&wire).map_err(|_| BackupError::ManifestInvalid)?;
        if bytes.len() > MAX_ENVELOPE_BYTES {
            return Err(BackupError::ManifestInvalid);
        }
        Ok(bytes)
    }

    /// Decodes and validates an untrusted envelope before restore staging.
    pub fn decode(bytes: &[u8]) -> Result<Self, BackupError> {
        if bytes.is_empty() || bytes.len() > MAX_ENVELOPE_BYTES {
            return Err(BackupError::ManifestInvalid);
        }
        let wire: EnvelopeWire =
            serde_json::from_slice(bytes).map_err(|_| BackupError::ManifestInvalid)?;
        if wire.header != HEADER {
            return Err(BackupError::ManifestInvalid);
        }
        let mut participants = Vec::with_capacity(wire.participants.len());
        for participant in wire.participants {
            let manifest = BackupManifest::decode(participant.manifest.as_bytes())?;
            let database_incarnation = u128::from_str_radix(&participant.database_incarnation, 16)
                .map_err(|_| BackupError::ManifestInvalid)?;
            participants.push(BackupEnvelopeParticipant {
                participant_id: participant.participant_id,
                database_id: participant.database_id,
                database_incarnation,
                route_generation: participant.route_generation,
                boundary: participant.boundary,
                manifest,
            });
        }
        let envelope = Self::assemble(
            wire.database_id,
            u128::from_str_radix(&wire.database_incarnation, 16)
                .map_err(|_| BackupError::ManifestInvalid)?,
            wire.route_generation,
            participants,
        )?;
        if envelope.digest != wire.digest {
            return Err(BackupError::ChecksumMismatch);
        }
        Ok(envelope)
    }

    /// Verifies supplied manifests match every participant before publication.
    pub fn verify_manifests(
        &self,
        manifests: &[(String, BackupManifest)],
    ) -> Result<(), BackupError> {
        if manifests.len() != self.participants.len() {
            return Err(BackupError::ChainIncomplete);
        }
        for (expected, (participant_id, manifest)) in self.participants.iter().zip(manifests) {
            if expected.participant_id != *participant_id
                || expected.manifest != *manifest
                || manifest.boundary_sequence().get() != expected.boundary
            {
                return Err(BackupError::ChecksumMismatch);
            }
        }
        Ok(())
    }

    /// Verifies every staged participant archive before a caller publishes the
    /// envelope directory or switches a restore target into serving state.
    pub fn verify_staged(&self, roots: &[(String, &Path)]) -> Result<(), BackupError> {
        if roots.len() != self.participants.len() {
            return Err(BackupError::ChainIncomplete);
        }
        for (expected, (participant_id, root)) in self.participants.iter().zip(roots) {
            if expected.participant_id != *participant_id {
                return Err(BackupError::ManifestInvalid);
            }
            expected.manifest.verify(root)?;
        }
        Ok(())
    }

    fn wire(&self) -> Result<EnvelopeWire, BackupError> {
        let participants = self
            .participants
            .iter()
            .map(|participant| {
                Ok(ParticipantWire {
                    participant_id: participant.participant_id.clone(),
                    database_id: participant.database_id.clone(),
                    database_incarnation: format!("{:032x}", participant.database_incarnation),
                    route_generation: participant.route_generation,
                    boundary: participant.boundary,
                    manifest: String::from_utf8(participant.manifest.encode())
                        .map_err(|_| BackupError::ManifestInvalid)?,
                })
            })
            .collect::<Result<Vec<_>, BackupError>>()?;
        Ok(EnvelopeWire {
            header: HEADER.into(),
            database_id: self.database_id.clone(),
            database_incarnation: format!("{:032x}", self.database_incarnation),
            route_generation: self.route_generation,
            participants,
            digest: self.digest.clone(),
        })
    }

    fn compute_digest(&self) -> Result<String, BackupError> {
        let mut wire = self.wire()?;
        wire.digest.clear();
        let bytes = serde_json::to_vec(&wire).map_err(|_| BackupError::ManifestInvalid)?;
        Ok(hex::encode(digest(&SHA256, &bytes).as_ref()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::backup::manifest::{BackupTenantCount, ManifestFacts};
    use crate::types::{StorageSequence, TenantId};

    fn manifest(boundary: u64) -> BackupManifest {
        BackupManifest::assemble(ManifestFacts {
            database_id: "0123456789abcdef0123456789abcdef".into(),
            partition_id: "partition-000000".into(),
            kind: super::super::BackupKind::Full,
            boundary: StorageSequence::new(boundary),
            boundary_unix_millis: boundary,
            captured_unix_millis: boundary,
            retain_until_unix_millis: boundary + 86_400_000,
            logical_checksum: "a".repeat(64),
            logical_documents: 0,
            documents: 0,
            family_records: 0,
            deletes: 0,
            disclosures: 0,
            tenants: vec![BackupTenantCount::new(
                TenantId::try_new("tenant-a").expect("valid tenant"),
                0,
                0,
            )],
            members: Vec::new(),
        })
    }

    #[test]
    fn rejects_malformed_envelope_before_restore() {
        assert_eq!(BackupEnvelope::decode(b"{}"), Err(BackupError::ManifestInvalid));
    }

    #[test]
    fn accepts_participants_with_independent_local_boundaries() {
        let first = manifest(7);
        let second = manifest(8);
        let participants = vec![
            BackupEnvelopeParticipant {
                participant_id: "shard-0000000000000001".into(),
                database_id: first.database_id().into(),
                database_incarnation: 1,
                route_generation: 1,
                boundary: first.boundary_sequence().get(),
                manifest: first,
            },
            BackupEnvelopeParticipant {
                participant_id: "shard-0000000000000002".into(),
                database_id: second.database_id().into(),
                database_incarnation: 1,
                route_generation: 1,
                boundary: second.boundary_sequence().get(),
                manifest: second,
            },
        ];

        assert!(BackupEnvelope::assemble("logical-database".into(), 1, 1, participants).is_ok());
    }

    #[test]
    fn rejects_participant_boundary_mismatch() {
        let point = manifest(7);
        let participant = BackupEnvelopeParticipant {
            participant_id: "shard-0000000000000001".into(),
            database_id: point.database_id().into(),
            database_incarnation: 1,
            route_generation: 1,
            boundary: 8,
            manifest: point,
        };

        assert_eq!(
            BackupEnvelope::assemble("logical-database".into(), 1, 1, vec![participant]),
            Err(BackupError::ManifestInvalid)
        );
    }
}
