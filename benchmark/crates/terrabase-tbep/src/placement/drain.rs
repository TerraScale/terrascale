//! Durable host transition intent, separate from completed drain receipts.

use super::transition::{TransitionTrust, VerifiedPreparedTransition, VerifiedProof};
use super::{PlacementError, PlacementJournal};
use rusqlite::{Connection, OptionalExtension as _, TransactionBehavior, params};

type StoredIntent = (String, Vec<u8>, String);

impl PlacementJournal {
    /// Persists authenticated intent to drain the currently accepted epoch.
    ///
    /// This immediately revokes that epoch from this journal's membership and
    /// WAL assignment sources, including after restart. The caller must still
    /// fence the actual engines and drain running workers before issuing any
    /// old-epoch receipt. This value does not certify completed drainage.
    /// Identical historical retries return their original verified intent.
    ///
    /// # Errors
    /// Refuses invalid authority, stale predecessors, conflicting retries, or
    /// unavailable durable storage. Failed validation does not fence the epoch.
    pub fn begin_transition_drain(
        &self,
        operation: &str,
        prepared: &[u8],
    ) -> Result<VerifiedPreparedTransition, PlacementError> {
        let configured = self.trust.lock().map_err(|_| PlacementError::Storage)?;
        let trust = configured.as_ref().ok_or(PlacementError::Invalid)?;
        let mut connection = self.connection.lock().map_err(|_| PlacementError::Storage)?;
        let transaction = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| PlacementError::Storage)?;
        let chain = self.verified_chain(&transaction, trust)?;
        let existing: Option<String> = transaction
            .query_row(
                "SELECT prior_digest FROM transition_drains WHERE operation=?1",
                [operation],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| PlacementError::Storage)?;
        if let Some(prior_digest) = existing {
            let (_, _, prior) = chain
                .iter()
                .find(|(_, _, prior)| prior.digest == prior_digest)
                .ok_or(PlacementError::Storage)?;
            let recorded =
                read_intent(&transaction, trust, prior)?.ok_or(PlacementError::Storage)?;
            let proposed = trust.decode_prepared(prepared, prior)?;
            if proposed.operation_id() != operation
                || proposed.proof_digest() != recorded.proof_digest()
            {
                return Err(PlacementError::RetryMismatch);
            }
            return Ok(recorded);
        }
        if chain.iter().any(|(_, _, proof)| proof.proof.request.operation_id == operation) {
            return Err(PlacementError::RetryMismatch);
        }
        let (_, _, prior) = chain.last().ok_or(PlacementError::NotFound)?;
        let verified = trust.decode_prepared(prepared, prior)?;
        if verified.operation_id() != operation {
            return Err(PlacementError::RetryMismatch);
        }
        if read_intent(&transaction, trust, prior)?.is_some() {
            return Err(PlacementError::Conflict);
        }
        transaction.execute(
            "INSERT INTO transition_drains (prior_digest,operation,prepared,prepared_digest) VALUES (?1,?2,?3,?4)",
            params![prior.digest, operation, prepared, verified.proof_digest()],
        ).map_err(|_| PlacementError::Storage)?;
        transaction.commit().map_err(|_| PlacementError::Storage)?;
        Ok(verified)
    }
}

pub(super) fn refuse_draining(
    connection: &Connection,
    trust: &TransitionTrust,
    prior: &VerifiedProof,
) -> Result<(), PlacementError> {
    if read_intent(connection, trust, prior)?.is_some() {
        return Err(PlacementError::Conflict);
    }
    Ok(())
}

fn read_intent(
    connection: &Connection,
    trust: &TransitionTrust,
    prior: &VerifiedProof,
) -> Result<Option<VerifiedPreparedTransition>, PlacementError> {
    let row: Option<StoredIntent> = connection.query_row(
        "SELECT operation,prepared,prepared_digest FROM transition_drains WHERE prior_digest=?1",
        [&prior.digest], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
    ).optional().map_err(|_| PlacementError::Storage)?;
    let Some((operation, bytes, digest)) = row else { return Ok(None) };
    let verified = trust.decode_prepared(&bytes, prior)?;
    if verified.operation_id() != operation || verified.proof_digest() != digest {
        return Err(PlacementError::Storage);
    }
    Ok(Some(verified))
}

pub(super) fn match_successor(
    connection: &Connection,
    trust: &TransitionTrust,
    prior: Option<&VerifiedProof>,
    successor: &VerifiedProof,
) -> Result<(), PlacementError> {
    let Some(prior) = prior else { return Ok(()) };
    let Some(intent) = read_intent(connection, trust, prior)? else { return Ok(()) };
    if intent.request_digest() != successor.proof.request_digest
        || successor.proof.authority_epoch < intent.authority_epoch()
        || successor.proof.journal_revision <= intent.journal_revision()
    {
        return Err(PlacementError::Conflict);
    }
    Ok(())
}

pub(super) fn validate_intents(
    connection: &Connection,
    trust: &TransitionTrust,
    chain: &[(u64, u64, VerifiedProof)],
) -> Result<(), PlacementError> {
    let mut statement = connection
        .prepare("SELECT prior_digest FROM transition_drains")
        .map_err(|_| PlacementError::Storage)?;
    let mut rows = statement.query([]).map_err(|_| PlacementError::Storage)?;
    let mut count = 0;
    while let Some(row) = rows.next().map_err(|_| PlacementError::Storage)? {
        count += 1;
        if count > chain.len() {
            return Err(PlacementError::Storage);
        }
        let digest: String = row.get(0).map_err(|_| PlacementError::Storage)?;
        let (_, _, prior) = chain
            .iter()
            .find(|(_, _, prior)| prior.digest == digest)
            .ok_or(PlacementError::Storage)?;
        read_intent(connection, trust, prior)?.ok_or(PlacementError::Storage)?;
    }
    Ok(())
}
