//! Fresh host admission from an explicit GRU checkpoint, never invented local history.

use super::transition::{Signed, TransitionReceiptSigner, TransitionTrust, VerifiedProof, hash};
use super::{PlacementError, PlacementJournal, pb};
use prost::Message as _;
use ring::rand::{SecureRandom as _, SystemRandom};
use rusqlite::{Connection, OptionalExtension as _, params};
use serde::{Deserialize, Serialize};

const MAX_ACCEPT_BYTES: usize = 256 * 1024;
const MAX_JOIN_LIFETIME_MS: i64 = 300_000;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct AcceptanceEnvelope {
    key_id: String,
    payload: String,
    signature: String,
    #[serde(default)]
    join_checkpoint: Option<Signed>,
}

pub(super) struct Acceptance {
    pub proof: Vec<u8>,
    pub checkpoint: Option<Vec<u8>>,
}

pub(super) fn decode_acceptance(encoded: &[u8]) -> Result<Acceptance, PlacementError> {
    if encoded.is_empty() || encoded.len() > MAX_ACCEPT_BYTES {
        return Err(PlacementError::Invalid);
    }
    let envelope: AcceptanceEnvelope = serde_json::from_slice(encoded).map_err(|_| PlacementError::Invalid)?;
    let proof = serde_json::to_vec(&Signed {
        key_id: envelope.key_id,
        payload: envelope.payload,
        signature: envelope.signature,
    }).map_err(|_| PlacementError::Invalid)?;
    let checkpoint = envelope.join_checkpoint.map(|checkpoint| serde_json::to_vec(&checkpoint).map_err(|_| PlacementError::Invalid)).transpose()?;
    Ok(Acceptance { proof, checkpoint })
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct JoinCheckpoint {
    schema_version: u32,
    kind: String,
    scope_id: String,
    member_identity: String,
    member_region: String,
    member_generation: u64,
    provider_resource_id: String,
    bootstrap_nonce: String,
    prior_proof: Signed,
    prior_proof_digest: String,
    accepted_proof_digest: String,
    successor_request_digest: String,
    stage_proposal_digest: String,
    proposal_revision: u64,
    authority_epoch: u64,
    lease_id: String,
    candidate_id: String,
    issued_at_unix_millis: i64,
    expires_at_unix_millis: i64,
}

pub(super) struct VerifiedJoin {
    pub prior: VerifiedProof,
    grant: JoinCheckpoint,
}

pub(super) fn initialize(connection: &Connection) -> Result<(), PlacementError> {
    connection.execute_batch(
        "CREATE TABLE IF NOT EXISTS placement_join_nonce (id INTEGER PRIMARY KEY CHECK(id=1), nonce TEXT NOT NULL);
         CREATE TABLE IF NOT EXISTS placement_join_checkpoint (id INTEGER PRIMARY KEY CHECK(id=1), checkpoint BLOB NOT NULL);"
    ).map_err(|_| PlacementError::Storage)?;
    if connection.query_row("SELECT nonce FROM placement_join_nonce WHERE id=1", [], |row| row.get::<_, String>(0)).optional().map_err(|_| PlacementError::Storage)?.is_none() {
        // Existing admission history without its nonce is corruption, never a
        // reason to silently mint a replacement bootstrap identity.
        let retained: bool = connection.query_row("SELECT EXISTS(SELECT 1 FROM placement_join_checkpoint)", [], |row| row.get(0)).map_err(|_| PlacementError::Storage)?;
        if retained { return Err(PlacementError::Storage); }
        let mut bytes = [0_u8; 32];
        SystemRandom::new().fill(&mut bytes).map_err(|_| PlacementError::Storage)?;
        let nonce = bytes.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
        connection.execute("INSERT INTO placement_join_nonce VALUES(1,?1)", [nonce]).map_err(|_| PlacementError::Storage)?;
    }
    nonce(connection).map(|_| ())
}

fn nonce(connection: &Connection) -> Result<String, PlacementError> {
    let value: String = connection.query_row("SELECT nonce FROM placement_join_nonce WHERE id=1", [], |row| row.get(0)).map_err(|_| PlacementError::Storage)?;
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)) {
        return Err(PlacementError::Storage);
    }
    Ok(value)
}

pub(super) fn stored(connection: &Connection) -> Result<Option<Vec<u8>>, PlacementError> {
    connection.query_row("SELECT checkpoint FROM placement_join_checkpoint WHERE id=1", [], |row| row.get(0)).optional().map_err(|_| PlacementError::Storage)
}

impl PlacementJournal {
    /// Signs the fresh journal's random challenge only for a persisted local proposal.
    /// Its deterministic bytes remain stable for retries after acceptance.
    pub fn sign_join_challenge(&self, response: &pb::PlacementProposalResponse, signer: &TransitionReceiptSigner) -> Result<Vec<u8>, PlacementError> {
        let connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let proposal = response.proposal.as_ref().ok_or(PlacementError::Invalid)?;
        let bytes: Vec<u8> = connection.query_row("SELECT proposal FROM proposals WHERE revision=?1", [i64::try_from(response.revision).map_err(|_| PlacementError::Invalid)?], |row| row.get(0)).map_err(|_| PlacementError::Storage)?;
        if bytes != proposal.encode_to_vec() { return Err(PlacementError::Invalid); }
        let identity = self.member_identity()?;
        let region = proposal.regions.iter().find(|region| region.members.contains(&self.boundary.member)).ok_or(PlacementError::Invalid)?;
        #[derive(Serialize)]
        struct Challenge<'a> {
            schema_version: u32,
            kind: &'static str,
            scope_id: String,
            member_identity: &'a str,
            member_region: &'a str,
            bootstrap_nonce: String,
            proposal_revision: u64,
            stage_proposal_digest: String,
        }
        signer.sign_join_challenge(&identity, &serde_json::to_vec(&Challenge {
            schema_version: 1,
            kind: "placement_join_challenge",
            scope_id: format!("{}/{}", self.boundary.environment, self.boundary.cluster),
            member_identity: &identity,
            member_region: &region.region,
            bootstrap_nonce: nonce(&connection)?,
            proposal_revision: response.revision,
            stage_proposal_digest: hash(&bytes),
        }).map_err(|_| PlacementError::Storage)?)
    }

    fn member_identity(&self) -> Result<String, PlacementError> {
        super::MemberWorkloadIdentity::try_new(&self.boundary.environment, &self.boundary.cluster, &self.boundary.member)
            .map(|identity| identity.as_str().to_owned()).map_err(|_| PlacementError::IdentityMismatch)
    }

    pub(super) fn verify_join(&self, connection: &Connection, trust: &TransitionTrust, encoded: &[u8], fresh: bool) -> Result<VerifiedJoin, PlacementError> {
        if encoded.len() > MAX_ACCEPT_BYTES { return Err(PlacementError::Invalid); }
        let payload = trust.verify_authority_payload(encoded)?;
        let grant: JoinCheckpoint = serde_json::from_slice(&payload).map_err(|_| PlacementError::Invalid)?;
        if grant.schema_version != 1 || grant.kind != "placement_join"
            || grant.scope_id != format!("{}/{}", self.boundary.environment, self.boundary.cluster)
            || grant.member_identity != self.member_identity()?
            || !matches!(grant.member_region.as_str(), "GRU" | "SFO" | "FRA")
            || grant.member_generation == 0 || grant.provider_resource_id.is_empty() || grant.provider_resource_id.len() > 512
            || grant.bootstrap_nonce != nonce(connection)?
            || grant.authority_epoch == 0 || grant.lease_id.is_empty() || grant.candidate_id.is_empty()
            || grant.issued_at_unix_millis < 0
            || grant.expires_at_unix_millis <= grant.issued_at_unix_millis
            || grant.expires_at_unix_millis.saturating_sub(grant.issued_at_unix_millis) > MAX_JOIN_LIFETIME_MS
        { return Err(PlacementError::Invalid); }
        if fresh {
            let now = now_millis()?;
            if now < grant.issued_at_unix_millis || now >= grant.expires_at_unix_millis { return Err(PlacementError::Conflict); }
            let floor: Option<i64> = connection.query_row("SELECT epoch FROM lifecycle_authority WHERE id=1", [], |row| row.get(0)).optional().map_err(|_| PlacementError::Storage)?;
            if floor.is_some_and(|floor| u64::try_from(floor).map_or(true, |floor| grant.authority_epoch < floor)) { return Err(PlacementError::Conflict); }
            let withdrawn: bool = connection.query_row("SELECT EXISTS(SELECT 1 FROM transition_drains) OR EXISTS(SELECT 1 FROM lifecycle_operations)", [], |row| row.get(0)).map_err(|_| PlacementError::Storage)?;
            if withdrawn { return Err(PlacementError::Conflict); }
        }
        // The fresh GRU checkpoint explicitly attests this exact durable prior
        // proof. Its old history is not installed as this target's history.
        let prior = trust.decode_checkpoint_prior(&serde_json::to_vec(&grant.prior_proof).map_err(|_| PlacementError::Invalid)?)?;
        if prior.digest != grant.prior_proof_digest || prior.proof.request.scope_id != grant.scope_id
            || prior.proof.authority_epoch > grant.authority_epoch
        { return Err(PlacementError::Invalid); }
        Ok(VerifiedJoin { prior, grant })
    }

    pub(super) fn match_join(&self, connection: &Connection, join: &VerifiedJoin, proof: &VerifiedProof, revision: u64) -> Result<(), PlacementError> {
        let grant = &join.grant;
        let stage: Vec<u8> = connection.query_row("SELECT proposal FROM proposals WHERE revision=?1", [i64::try_from(revision).map_err(|_| PlacementError::Invalid)?], |row| row.get(0)).map_err(|_| PlacementError::Storage)?;
        if grant.proposal_revision != revision || grant.stage_proposal_digest != hash(&stage)
            || grant.accepted_proof_digest != proof.digest
            || grant.successor_request_digest != proof.proof.request_digest
            || grant.authority_epoch < proof.proof.authority_epoch
            || !proof.proof.request.members.iter().any(|member| member.identity == grant.member_identity && member.region == grant.member_region)
        { return Err(PlacementError::Invalid); }
        proof.follows(Some(&join.prior))
    }
}

pub(super) fn persist(connection: &Connection, checkpoint: &[u8]) -> Result<(), PlacementError> {
    connection.execute("INSERT INTO placement_join_checkpoint VALUES(1,?1)", params![checkpoint]).map_err(|_| PlacementError::Storage)?;
    Ok(())
}

fn now_millis() -> Result<i64, PlacementError> {
    i64::try_from(std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_err(|_| PlacementError::Storage)?.as_millis()).map_err(|_| PlacementError::Storage)
}
