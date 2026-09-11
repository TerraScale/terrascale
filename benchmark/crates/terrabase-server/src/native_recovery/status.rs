use super::{NativeRecoveryError, NativeRecoveryOperation, NativeRecoveryPhase};
use rusqlite::{Connection, OptionalExtension, params};

const MAX_RECORD_BYTES: i64 = 262_144;

pub(super) fn read(
    connection: &Connection,
    source: &str,
    operation: &str,
) -> Result<Option<NativeRecoveryOperation>, NativeRecoveryError> {
    if source.is_empty()
        || operation.is_empty()
        || source.len() > 256
        || operation.len() > 256
        || source.chars().any(char::is_control)
        || operation.chars().any(char::is_control)
    {
        return Err(NativeRecoveryError::InvalidRequest);
    }
    connection
        .query_row(
            "SELECT source_database_id, operation_id, phase, length(record), record
             FROM native_recovery_operations WHERE source_database_id=?1 AND operation_id=?2",
            params![source, operation],
            |row| {
                let row_source: String = row.get(0)?;
                let row_operation: String = row.get(1)?;
                let phase: String = row.get(2)?;
                let size: i64 = row.get(3)?;
                let record: Vec<u8> = row.get(4)?;
                Ok((row_source, row_operation, phase, size, record))
            },
        )
        .optional()
        .map_err(|_| NativeRecoveryError::Unavailable)?
        .map(|(row_source, row_operation, phase, size, record)| {
            if row_source != source
                || row_operation != operation
                || !(1..=MAX_RECORD_BYTES).contains(&size)
            {
                return Err(NativeRecoveryError::VerificationFailed);
            }
            let state: NativeRecoveryOperation = serde_json::from_slice(&record)
                .map_err(|_| NativeRecoveryError::VerificationFailed)?;
            validate(&state, source, operation, &phase)?;
            let checkpoint = super::persistence::load_checkpoint(connection, source, operation)?;
            if let Some(point) = checkpoint.as_ref() {
                super::checkpoint::validate(Some(point), source, &point.placement)?;
            }
            if state.phase == NativeRecoveryPhase::Complete {
                let point = checkpoint.as_ref().ok_or(NativeRecoveryError::VerificationFailed)?;
                if !state.facts.as_ref().is_some_and(|facts| point.matches_facts(facts)) {
                    return Err(NativeRecoveryError::VerificationFailed);
                }
            }
            Ok(state)
        })
        .transpose()
}

pub(super) fn validate(
    state: &NativeRecoveryOperation,
    source: &str,
    operation: &str,
    sql_phase: &str,
) -> Result<(), NativeRecoveryError> {
    let expected = match state.phase {
        NativeRecoveryPhase::Accepted => "accepted",
        NativeRecoveryPhase::Capturing => "capturing",
        NativeRecoveryPhase::Measuring => "measuring",
        NativeRecoveryPhase::Complete => "complete",
        NativeRecoveryPhase::Failed => "failed",
    };
    if state.operation_id != operation
        || state.source_database_id != source
        || sql_phase != expected
        || (matches!(state.phase, NativeRecoveryPhase::Complete)
            && (state.facts.is_none() || state.error.is_some()))
        || (!matches!(state.phase, NativeRecoveryPhase::Complete) && state.facts.is_some())
    {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    if let Some(facts) = &state.facts
        && (facts.source_database_id != source
            || facts.recovery_point_id.is_empty()
            || facts.recovery_point_id.len() > 256
            || blake3::Hash::from_hex(&facts.checksum).is_err()
            || !(1..=20).contains(&facts.placement.len())
            || facts.placement.iter().any(|region| {
                region.is_empty() || region.len() > 128 || region.chars().any(char::is_control)
            })
            || facts.retained_until_millis < facts.captured_at_millis)
    {
        return Err(NativeRecoveryError::VerificationFailed);
    }
    Ok(())
}
