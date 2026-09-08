use super::NativeRecoveryError;
use super::{NativeRecoveryOperation, NativeRecoveryPhase, RecoveryPointFacts};
use serde::{Deserialize, Serialize};

const MAX_BYTES: usize = 262_144;

/// Immutable provider-bound capture metadata kept outside the public operation record.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Checkpoint {
    pub(super) recovery_point_id: String,
    pub(super) source_database_id: String,
    pub(super) journal_cut: u64,
    pub(super) captured_at_millis: u64,
    pub(super) retained_until_millis: u64,
    pub(super) placement: Vec<String>,
}

impl Checkpoint {
    /// Builds a checkpoint for a logical multi-participant envelope. The
    /// envelope digest is the recovery identity; no participant is promoted
    /// to represent the logical point.
    pub(super) fn from_envelope(
        envelope: &terrabase::backup::BackupEnvelope,
        source: &str,
        placement: &[String],
    ) -> Self {
        Self {
            recovery_point_id: envelope.digest().to_owned(),
            source_database_id: source.to_owned(),
            journal_cut: envelope
                .participants()
                .iter()
                .map(|participant| participant.boundary)
                .max()
                .unwrap_or_default(),
            captured_at_millis: envelope.captured_unix_millis(),
            retained_until_millis: envelope.retain_until_unix_millis(),
            placement: placement.to_vec(),
        }
    }

    /// Verifies that this checkpoint still refers to the same complete
    /// logical envelope. The scalar journal cut is only a compatibility
    /// summary; the envelope digest authenticates every participant boundary.
    pub(super) fn matches_envelope(
        &self,
        envelope: &terrabase::backup::BackupEnvelope,
        source: &str,
        placement: &[String],
    ) -> bool {
        self.recovery_point_id == envelope.digest()
            && self.source_database_id == source
            && self.journal_cut
                == envelope
                    .participants()
                    .iter()
                    .map(|participant| participant.boundary)
                    .max()
                    .unwrap_or_default()
            && self.captured_at_millis == envelope.captured_unix_millis()
            && self.retained_until_millis == envelope.retain_until_unix_millis()
            && self.placement == placement
    }

    pub(super) fn from_manifest(
        manifest: &terrabase::backup::BackupManifest,
        source: &str,
        placement: &[String],
    ) -> Self {
        Self {
            recovery_point_id: manifest.identity().to_owned(),
            source_database_id: source.to_owned(),
            journal_cut: manifest.boundary_sequence().get(),
            captured_at_millis: manifest.captured_unix_millis(),
            retained_until_millis: manifest.retain_until_unix_millis(),
            placement: placement.to_vec(),
        }
    }

    pub(super) fn encode(&self) -> Result<Vec<u8>, serde_json::Error> {
        serde_json::to_vec(self)
    }

    pub(super) fn decode(bytes: &[u8]) -> Result<Self, NativeRecoveryError> {
        if bytes.is_empty() || bytes.len() > MAX_BYTES {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        serde_json::from_slice(bytes).map_err(|_| NativeRecoveryError::VerificationFailed)
    }

    pub(super) fn matches_manifest(&self, manifest: &terrabase::backup::BackupManifest) -> bool {
        self.recovery_point_id == manifest.identity()
            && self.source_database_id == manifest.database_id()
            && self.journal_cut == manifest.boundary_sequence().get()
            && self.captured_at_millis == manifest.captured_unix_millis()
            && self.retained_until_millis == manifest.retain_until_unix_millis()
    }

    pub(super) fn matches_facts(&self, facts: &RecoveryPointFacts) -> bool {
        self.recovery_point_id == facts.recovery_point_id
            && self.source_database_id == facts.source_database_id
            && self.journal_cut == facts.journal_cut
            && self.captured_at_millis == facts.captured_at_millis
            && self.retained_until_millis == facts.retained_until_millis
            && self.placement == facts.placement
    }
}

pub(super) fn validate(
    checkpoint: Option<&Checkpoint>,
    source: &str,
    placement: &[String],
) -> Result<(), NativeRecoveryError> {
    let Some(point) = checkpoint else {
        return Ok(());
    };
    if point.source_database_id != source
        || point.recovery_point_id.is_empty()
        || point.recovery_point_id.len() > 256
        || point.placement != placement
        || point.placement.is_empty()
        || point.placement.len() > 20
        || point.placement.iter().any(|r| r.is_empty() || r.chars().any(char::is_control))
        || point.retained_until_millis < point.captured_at_millis
    {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    Ok(())
}

pub(super) fn advance<C, P, M>(
    state: &mut NativeRecoveryOperation,
    mut checkpoint: Option<Checkpoint>,
    placement: &[String],
    capture: C,
    mut persist: P,
    measure: M,
) -> Result<(), NativeRecoveryError>
where
    C: FnOnce() -> Result<Checkpoint, NativeRecoveryError>,
    P: FnMut(&NativeRecoveryOperation, Option<&Checkpoint>) -> Result<(), NativeRecoveryError>,
    M: FnOnce(&Checkpoint) -> Result<RecoveryPointFacts, NativeRecoveryError>,
{
    if matches!(state.phase, NativeRecoveryPhase::Complete) {
        let point = checkpoint.as_ref().ok_or(NativeRecoveryError::VerificationFailed)?;
        validate(Some(point), &state.source_database_id, placement)?;
        let facts = state.facts.as_ref().ok_or(NativeRecoveryError::VerificationFailed)?;
        if !point.matches_facts(facts) {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        return Ok(());
    }
    if matches!(state.phase, NativeRecoveryPhase::Capturing) && checkpoint.is_none() {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    if matches!(state.phase, NativeRecoveryPhase::Failed)
        || (matches!(state.phase, NativeRecoveryPhase::Measuring) && checkpoint.is_none())
    {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    validate(checkpoint.as_ref(), &state.source_database_id, placement)?;
    if checkpoint.is_none() {
        if !matches!(state.phase, NativeRecoveryPhase::Accepted) {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        state.phase = NativeRecoveryPhase::Capturing;
        state.error = None;
        persist(state, None)?;
        checkpoint = Some(capture()?);
        validate(checkpoint.as_ref(), &state.source_database_id, placement)?;
    }
    if matches!(state.phase, NativeRecoveryPhase::Accepted) {
        state.phase = NativeRecoveryPhase::Measuring;
        state.error = None;
        persist(state, checkpoint.as_ref())?;
    } else if matches!(state.phase, NativeRecoveryPhase::Capturing) {
        state.phase = NativeRecoveryPhase::Measuring;
        state.error = None;
        persist(state, checkpoint.as_ref())?;
    }
    let facts = measure(checkpoint.as_ref().ok_or(NativeRecoveryError::VerificationFailed)?)?;
    if !checkpoint.as_ref().is_some_and(|point| point.matches_facts(&facts)) {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    state.facts = Some(facts);
    state.error = None;
    state.phase = NativeRecoveryPhase::Complete;
    persist(state, checkpoint.as_ref())
}

#[cfg(test)]
mod tests;
