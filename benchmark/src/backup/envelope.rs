//! Authenticated multi-participant backup envelopes.
//!
//! A database with admitted shard participants cannot publish independent
//! recovery points and call them one point.  This module binds the logical
//! database incarnation, route generation, and
//! ordered per-participant manifest identities into one digest.  Callers must
//! validate every participant before exposing the envelope as recoverable.

use super::{BackupCaptureKind, BackupError, BackupManifest};
use aws_lc_rs::digest::{SHA256, digest};
use serde::{Deserialize, Serialize};
use std::path::Path;

const MAX_PARTICIPANTS: usize = 64;
const MAX_ENVELOPE_BYTES: usize = 256 * 1024;
const HEADER: &str = "terrabase-backup-envelope 2";

/// One participant's immutable recovery point inside an envelope.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupEnvelopeParticipant {
    /// Exact physical identity captured with the admitted route catalog.
    pub file_identity: crate::shard_split::FileShardIdentity,
    /// Identifies the logical database root, independently of shard ordering.
    pub is_root: bool,
    /// Canonical admitted ranges owned by this file.
    pub routes: Vec<crate::shard_split::ShardRoute>,
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
    shard_id: u64,
    region: String,
    origin_lane_incarnation: String,
    is_root: bool,
    routes: Vec<RouteWire>,
    participant_id: String,
    database_id: String,
    database_incarnation: String,
    route_generation: u64,
    boundary: u64,
    manifest: String,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct RouteWire {
    lower: Option<Vec<u8>>,
    upper: Option<Vec<u8>>,
}

impl BackupEnvelope {
    /// Authenticates the complete routing inventory captured under admission.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ChainIncomplete`] when the cut and manifest
    /// participant counts differ, or [`BackupError::ManifestInvalid`] when
    /// the admitted identities or routes are inconsistent.
    pub fn from_read_cut(
        cut: &crate::member::AdmittedShardReadCut,
        manifests: Vec<BackupManifest>,
    ) -> Result<Self, BackupError> {
        let generation = cut.route_generation.ok_or(BackupError::ManifestInvalid)?;
        if manifests.len() != cut.participants.len() {
            return Err(BackupError::ChainIncomplete);
        }
        Self::assemble(
            cut.database_id.clone(),
            cut.database_incarnation,
            generation,
            cut.participants
                .iter()
                .zip(manifests)
                .map(|(participant, manifest)| BackupEnvelopeParticipant {
                    file_identity: participant.identity,
                    is_root: participant.identity.shard_id == cut.root_shard_id,
                    routes: participant.routes.clone(),
                    participant_id: format!("shard-{:016x}", participant.identity.shard_id),
                    database_id: cut.database_id.clone(),
                    database_incarnation: cut.database_incarnation,
                    route_generation: generation,
                    boundary: participant.snapshot.sequence().get(),
                    manifest,
                })
                .collect(),
        )
    }

    /// Assembles and validates an envelope from ordered participant manifests.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] when any envelope identity,
    /// route, participant, or manifest invariant is violated.
    pub fn assemble(
        database_id: String,
        database_incarnation: u128,
        route_generation: u64,
        mut participants: Vec<BackupEnvelopeParticipant>,
    ) -> Result<Self, BackupError> {
        if database_id
            != crate::identity::DatabaseUlid::from_wire_bytes(database_incarnation.to_be_bytes())
                .to_text()
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
        if participants.iter().filter(|participant| participant.is_root).count() != 1 {
            return Err(BackupError::ManifestInvalid);
        }
        let mut lanes = std::collections::BTreeSet::new();
        let region = participants[0].file_identity.region;
        for participant in &participants {
            if participant.file_identity.database_incarnation != database_incarnation
                || participant.file_identity.region != region
                || !lanes.insert(participant.file_identity.origin_lane_incarnation)
                || participant.file_identity.shard_id == 0
                || participant.file_identity.origin_lane_incarnation == 0
                || participant.participant_id
                    != format!("shard-{:016x}", participant.file_identity.shard_id)
                || participant.routes.is_empty()
                || participant
                    .routes
                    .iter()
                    .any(|route| route.shard_id() != participant.file_identity.shard_id)
                || participant.participant_id.is_empty()
                || participant.database_id.is_empty()
                || participant.database_id != database_id
                || participant.database_incarnation != database_incarnation
                || participant.route_generation != route_generation
                || participant.manifest.database_id() != participant.database_id
                || participant.manifest.boundary_sequence().get() != participant.boundary
                || participant.manifest.capture_kind() != BackupCaptureKind::RoutedShardParticipant
            {
                return Err(BackupError::ManifestInvalid);
            }
        }
        crate::shard_split::RouteTable::new(
            participants
                .iter()
                .flat_map(|participant| participant.routes.iter().cloned())
                .collect(),
        )
        .map_err(|_| BackupError::ManifestInvalid)?;
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
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] when the envelope cannot be
    /// serialized or exceeds the bounded wire size.
    pub fn encode(&self) -> Result<Vec<u8>, BackupError> {
        let wire = self.wire()?;
        let bytes = serde_json::to_vec(&wire).map_err(|_| BackupError::ManifestInvalid)?;
        if bytes.len() > MAX_ENVELOPE_BYTES {
            return Err(BackupError::ManifestInvalid);
        }
        Ok(bytes)
    }

    /// Decodes and validates an untrusted envelope before restore staging.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] for malformed or invalid
    /// envelope data, and [`BackupError::ChecksumMismatch`] for a bad digest.
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
                file_identity: crate::shard_split::FileShardIdentity {
                    database_incarnation,
                    shard_id: participant.shard_id,
                    region: participant.region.parse().map_err(|_| BackupError::ManifestInvalid)?,
                    origin_lane_incarnation: u128::from_str_radix(
                        &participant.origin_lane_incarnation,
                        16,
                    )
                    .map_err(|_| BackupError::ManifestInvalid)?,
                },
                is_root: participant.is_root,
                routes: participant
                    .routes
                    .into_iter()
                    .map(|route| {
                        crate::shard_split::ShardRoute::new(
                            participant.shard_id,
                            route.lower,
                            route.upper,
                        )
                        .map_err(|_| BackupError::ManifestInvalid)
                    })
                    .collect::<Result<Vec<_>, _>>()?,
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
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ChainIncomplete`] for incomplete input and
    /// [`BackupError::ChecksumMismatch`] for mismatched participant facts.
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
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ChainIncomplete`] for incomplete input,
    /// [`BackupError::ManifestInvalid`] for a mismatched participant, or the
    /// member verification error returned by the manifest.
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
                    shard_id: participant.file_identity.shard_id,
                    region: participant.file_identity.region.to_string(),
                    origin_lane_incarnation: format!(
                        "{:032x}",
                        participant.file_identity.origin_lane_incarnation
                    ),
                    is_root: participant.is_root,
                    routes: participant
                        .routes
                        .iter()
                        .map(|route| RouteWire {
                            lower: route.lower().map(<[u8]>::to_vec),
                            upper: route.upper().map(<[u8]>::to_vec),
                        })
                        .collect(),
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

    fn database() -> String {
        crate::identity::DatabaseUlid::from_wire_bytes(1_u128.to_be_bytes()).to_text()
    }

    fn identity(shard_id: u64) -> crate::shard_split::FileShardIdentity {
        crate::shard_split::FileShardIdentity {
            database_incarnation: 1,
            shard_id,
            region: crate::regions::RegionCode::GRU,
            origin_lane_incarnation: u128::from(shard_id),
        }
    }

    fn manifest(boundary: u64, capture_kind: BackupCaptureKind) -> BackupManifest {
        BackupManifest::assemble(ManifestFacts {
            database_id: database(),
            partition_id: "partition-000000".into(),
            kind: super::super::BackupKind::Full,
            capture_kind,
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
        let first = manifest(7, BackupCaptureKind::RoutedShardParticipant);
        let second = manifest(8, BackupCaptureKind::RoutedShardParticipant);
        let participants = vec![
            BackupEnvelopeParticipant {
                file_identity: identity(1),
                is_root: true,
                routes: vec![crate::shard_split::ShardRoute::new(1, None, Some(vec![7])).unwrap()],
                participant_id: "shard-0000000000000001".into(),
                database_id: first.database_id().into(),
                database_incarnation: 1,
                route_generation: 1,
                boundary: first.boundary_sequence().get(),
                manifest: first,
            },
            BackupEnvelopeParticipant {
                file_identity: identity(2),
                is_root: false,
                routes: vec![crate::shard_split::ShardRoute::new(2, Some(vec![7]), None).unwrap()],
                participant_id: "shard-0000000000000002".into(),
                database_id: second.database_id().into(),
                database_incarnation: 1,
                route_generation: 1,
                boundary: second.boundary_sequence().get(),
                manifest: second,
            },
        ];

        let envelope = BackupEnvelope::assemble(database(), 1, 1, participants).unwrap();
        assert_eq!(BackupEnvelope::decode(&envelope.encode().unwrap()).unwrap(), envelope);
        let mut wire: serde_json::Value =
            serde_json::from_slice(&envelope.encode().unwrap()).unwrap();
        wire["participants"][0]["origin_lane_incarnation"] =
            serde_json::json!("00000000000000000000000000000009");
        assert_eq!(
            BackupEnvelope::decode(&serde_json::to_vec(&wire).unwrap()),
            Err(BackupError::ChecksumMismatch)
        );
        let mut missing_root = envelope.participants().to_vec();
        missing_root[0].is_root = false;
        assert_eq!(
            BackupEnvelope::assemble(database(), 1, 1, missing_root),
            Err(BackupError::ManifestInvalid)
        );
        let mut overlap = envelope.participants().to_vec();
        overlap[1].routes =
            vec![crate::shard_split::ShardRoute::new(2, Some(vec![6]), None).unwrap()];
        assert_eq!(
            BackupEnvelope::assemble(database(), 1, 1, overlap),
            Err(BackupError::ManifestInvalid)
        );
    }

    #[test]
    fn rejects_participant_boundary_mismatch() {
        let point = manifest(7, BackupCaptureKind::RoutedShardParticipant);
        let participant = BackupEnvelopeParticipant {
            file_identity: identity(1),
            is_root: true,
            routes: vec![crate::shard_split::ShardRoute::new(1, None, None).unwrap()],
            participant_id: "shard-0000000000000001".into(),
            database_id: point.database_id().into(),
            database_incarnation: 1,
            route_generation: 1,
            boundary: 8,
            manifest: point,
        };

        assert_eq!(
            BackupEnvelope::assemble(database(), 1, 1, vec![participant]),
            Err(BackupError::ManifestInvalid)
        );
    }

    #[test]
    fn rejects_whole_database_manifest_as_routed_participant() {
        let point = manifest(7, BackupCaptureKind::WholeDatabase);
        let participant = BackupEnvelopeParticipant {
            file_identity: identity(1),
            is_root: true,
            routes: vec![crate::shard_split::ShardRoute::new(1, None, None).unwrap()],
            participant_id: "shard-0000000000000001".into(),
            database_id: point.database_id().into(),
            database_incarnation: 1,
            route_generation: 1,
            boundary: point.boundary_sequence().get(),
            manifest: point,
        };

        assert_eq!(
            BackupEnvelope::assemble(database(), 1, 1, vec![participant]),
            Err(BackupError::ManifestInvalid)
        );
    }
}
