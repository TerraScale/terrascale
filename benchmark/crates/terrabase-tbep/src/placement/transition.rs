//! Verification of durably issued GRU epoch proofs. Raw signed payload bytes are
//! authenticated before strict decoding; NATS liveness is never a membership fact.

use super::PlacementError;
use base64::Engine as _;
use ring::{digest, signature};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

const DOMAIN: &[u8] = b"terrabase-placement-transition-v1\0";
const MAX_PROOF_BYTES: usize = 256 * 1024;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Signed {
    pub key_id: String,
    pub payload: String,
    pub signature: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(super) struct TransitionMember {
    pub identity: String,
    pub region: String,
    pub placement_digest: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Request {
    pub operation_id: String,
    pub scope_id: String,
    pub prior_epoch: u64,
    pub epoch: u64,
    pub generation: u64,
    pub members: Vec<TransitionMember>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Proof {
    pub schema_version: u32,
    pub kind: String,
    pub request: Request,
    pub request_digest: String,
    pub authority_epoch: u64,
    pub journal_revision: u64,
    pub genesis_grant_id: String,
    pub receipt_digests: Vec<String>,
    pub drain_receipts: Vec<Signed>,
    pub prior_generation: u64,
    pub previous_proof_digest: String,
    pub lease_id: String,
    pub genesis_grant: Option<Signed>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Genesis {
    kind: String,
    grant_id: String,
    request_digest: String,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct FenceReceipt {
    pub kind: String,
    pub member_identity: String,
    pub scope_id: String,
    pub prior_epoch: u64,
    pub prior_generation: u64,
    pub request_digest: String,
    pub frontier_digest: String,
}

/// Explicitly provisioned public signing pins for GRU grants and member fences.
/// These are independent of endpoint health, proposal bytes, and supplied keys.
#[derive(Clone)]
pub struct TransitionTrust {
    authority: BTreeMap<String, Vec<u8>>,
    genesis: BTreeMap<String, Vec<u8>>,
    members: BTreeMap<String, Vec<u8>>,
}

impl TransitionTrust {
    /// Validates bounded Ed25519 public-key pins supplied by the host authority.
    ///
    /// # Errors
    /// Rejects empty authority/genesis pins, malformed identities or non-32-byte keys.
    pub fn new(
        authority: BTreeMap<String, Vec<u8>>,
        genesis: BTreeMap<String, Vec<u8>>,
        members: BTreeMap<String, Vec<u8>>,
    ) -> Result<Self, PlacementError> {
        if authority.is_empty()
            || genesis.is_empty()
            || members.len() > 40
            || authority.len() > 32
            || genesis.len() > 32
            || authority
                .iter()
                .chain(genesis.iter())
                .chain(members.iter())
                .any(|(id, key)| !token(id) || key.len() != 32)
        {
            return Err(PlacementError::Invalid);
        }
        Ok(Self { authority, genesis, members })
    }

    pub(super) fn decode(&self, encoded: &[u8]) -> Result<VerifiedProof, PlacementError> {
        if encoded.len() > MAX_PROOF_BYTES {
            return Err(PlacementError::Invalid);
        }
        let signed: Signed =
            serde_json::from_slice(encoded).map_err(|_| PlacementError::Invalid)?;
        let payload = verify(&signed, &self.authority)?;
        let proof: Proof = serde_json::from_slice(&payload).map_err(|_| PlacementError::Invalid)?;
        if proof.schema_version != 1
            || proof.kind != "accepted_epoch"
            || proof.authority_epoch == 0
            || proof.journal_revision == 0
            || !token(&proof.lease_id)
            || proof.request.epoch <= proof.request.prior_epoch
            || proof.request.generation == 0
            || !token(&proof.request.operation_id)
            || !token(&proof.request.scope_id)
            || proof.request.members.len() < 2
            || proof.request.members.len() > 40
            || proof.request_digest
                != hash(&serde_json::to_vec(&proof.request).map_err(|_| PlacementError::Invalid)?)
        {
            return Err(PlacementError::Invalid);
        }
        let mut regions: BTreeMap<&str, usize> = BTreeMap::new();
        let mut previous: Option<&str> = None;
        for member in &proof.request.members {
            if !token(&member.identity)
                || !valid_hash(&member.placement_digest)
                || !matches!(member.region.as_str(), "GRU" | "SFO" | "FRA")
                || previous.is_some_and(|old| old >= member.identity.as_str())
            {
                return Err(PlacementError::Invalid);
            }
            previous = Some(&member.identity);
            *regions.entry(&member.region).or_default() += 1;
        }
        if regions.values().any(|count| *count != 2) {
            return Err(PlacementError::Invalid);
        }
        let mut fences = Vec::new();
        if proof.request.prior_epoch == 0 {
            let grant = proof.genesis_grant.as_ref().ok_or(PlacementError::Invalid)?;
            let genesis: Genesis = serde_json::from_slice(&verify(grant, &self.genesis)?)
                .map_err(|_| PlacementError::Invalid)?;
            if genesis.kind != "genesis_grant"
                || !token(&genesis.grant_id)
                || genesis.grant_id != proof.genesis_grant_id
                || genesis.request_digest != proof.request_digest
                || proof.prior_generation != 0
                || !proof.previous_proof_digest.is_empty()
                || !proof.drain_receipts.is_empty()
                || !proof.receipt_digests.is_empty()
            {
                return Err(PlacementError::Invalid);
            }
        } else {
            if proof.genesis_grant.is_some()
                || !proof.genesis_grant_id.is_empty()
                || !valid_hash(&proof.previous_proof_digest)
                || proof.prior_generation == 0
                || proof.request.generation <= proof.prior_generation
                || proof.drain_receipts.len() > 40
            {
                return Err(PlacementError::Invalid);
            }
            let mut digests = Vec::new();
            let mut identities = BTreeSet::new();
            for signed in &proof.drain_receipts {
                let payload = verify(signed, &self.members)?;
                let receipt: FenceReceipt =
                    serde_json::from_slice(&payload).map_err(|_| PlacementError::Invalid)?;
                if receipt.kind != "old_epoch_drained_fenced"
                    || receipt.member_identity != signed.key_id
                    || receipt.scope_id != proof.request.scope_id
                    || receipt.prior_epoch != proof.request.prior_epoch
                    || receipt.prior_generation != proof.prior_generation
                    || receipt.request_digest != proof.request_digest
                    || !valid_hash(&receipt.frontier_digest)
                    || !identities.insert(receipt.member_identity.clone())
                {
                    return Err(PlacementError::Invalid);
                }
                digests.push(hash(&payload));
                fences.push(receipt);
            }
            digests.sort();
            if digests != proof.receipt_digests {
                return Err(PlacementError::Invalid);
            }
        }
        Ok(VerifiedProof { proof, digest: hash(&payload), fences })
    }
}

pub(super) struct VerifiedProof {
    pub proof: Proof,
    pub digest: String,
    pub fences: Vec<FenceReceipt>,
}

impl VerifiedProof {
    pub fn follows(&self, prior: Option<&Self>) -> Result<(), PlacementError> {
        match prior {
            None if self.proof.request.prior_epoch == 0 => Ok(()),
            Some(prior) => {
                if self.proof.request.prior_epoch != prior.proof.request.epoch
                    || self.proof.prior_generation != prior.proof.request.generation
                    || self.proof.previous_proof_digest != prior.digest
                    || self.proof.authority_epoch < prior.proof.authority_epoch
                    || self.proof.journal_revision <= prior.proof.journal_revision
                {
                    return Err(PlacementError::Conflict);
                }
                let mut covered = BTreeSet::new();
                for receipt in &self.fences {
                    let member = prior
                        .proof
                        .request
                        .members
                        .iter()
                        .find(|member| member.identity == receipt.member_identity)
                        .ok_or(PlacementError::Invalid)?;
                    covered.insert(member.region.as_str());
                }
                if prior
                    .proof
                    .request
                    .members
                    .iter()
                    .any(|member| !covered.contains(member.region.as_str()))
                {
                    return Err(PlacementError::Invalid);
                }
                Ok(())
            }
            None => Err(PlacementError::Conflict),
        }
    }
}

fn verify(signed: &Signed, pins: &BTreeMap<String, Vec<u8>>) -> Result<Vec<u8>, PlacementError> {
    let key = pins.get(&signed.key_id).ok_or(PlacementError::Invalid)?;
    let payload = base64::engine::general_purpose::STANDARD
        .decode(&signed.payload)
        .map_err(|_| PlacementError::Invalid)?;
    let signature_bytes = base64::engine::general_purpose::STANDARD
        .decode(&signed.signature)
        .map_err(|_| PlacementError::Invalid)?;
    if payload.is_empty() || payload.len() > MAX_PROOF_BYTES || signature_bytes.len() != 64 {
        return Err(PlacementError::Invalid);
    }
    let mut input = Vec::with_capacity(DOMAIN.len() + payload.len());
    input.extend_from_slice(DOMAIN);
    input.extend_from_slice(&payload);
    signature::UnparsedPublicKey::new(&signature::ED25519, key)
        .verify(&input, &signature_bytes)
        .map_err(|_| PlacementError::Invalid)?;
    Ok(payload)
}

pub(super) fn hash(bytes: &[u8]) -> String {
    digest::digest(&digest::SHA256, bytes)
        .as_ref()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}
fn valid_hash(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}
fn token(value: &str) -> bool {
    !value.is_empty() && value.len() <= 256 && value.bytes().all(|byte| (33..=126).contains(&byte))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn independent_go_genesis_vector_verifies_without_reserializing_signed_payload() {
        let fixture: serde_json::Value =
            serde_json::from_str(include_str!("transition/genesis.json")).unwrap();
        let public = fixture["public_key_hex"].as_str().unwrap();
        let key = (0..public.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&public[i..i + 2], 16).unwrap())
            .collect::<Vec<_>>();
        let signed: Signed = serde_json::from_value(fixture["proof"].clone()).unwrap();
        let payload = base64::engine::general_purpose::STANDARD.decode(&signed.payload).unwrap();
        let proof: Proof = serde_json::from_slice(&payload).unwrap();
        let genesis_id = proof.genesis_grant.as_ref().unwrap().key_id.clone();
        let trust = TransitionTrust::new(
            BTreeMap::from([(signed.key_id.clone(), key.clone())]),
            BTreeMap::from([(genesis_id, key)]),
            BTreeMap::new(),
        )
        .unwrap();
        let encoded = serde_json::to_vec(&signed).unwrap();
        let verified = trust.decode(&encoded).unwrap();
        verified.follows(None).unwrap();
        assert_eq!(verified.proof.request_digest, fixture["request_digest"].as_str().unwrap());
        let mut forged = signed;
        forged.payload = base64::engine::general_purpose::STANDARD.encode(b"{}");
        assert!(trust.decode(&serde_json::to_vec(&forged).unwrap()).is_err());
    }
}
