use super::{Checkpoint, NativeRecoveryError, NativeRecoveryOperation};
use rusqlite::{Connection, params};

pub(super) fn load_checkpoint(
    connection: &Connection,
    source: &str,
    operation: &str,
) -> Result<Option<Checkpoint>, NativeRecoveryError> {
    connection
        .query_row(
            "SELECT checkpoint FROM native_recovery_operations WHERE source_database_id=?1 AND operation_id=?2",
            params![source, operation],
            |row| row.get::<_, Option<Vec<u8>>>(0),
        )
        .map_err(|_| NativeRecoveryError::Unavailable)?
        .map(|bytes| Checkpoint::decode(&bytes))
        .transpose()
}

pub(super) fn save(
    connection: &Connection,
    operation: &NativeRecoveryOperation,
    checkpoint: Option<&Checkpoint>,
) -> Result<(), NativeRecoveryError> {
    let bytes = serde_json::to_vec(operation).map_err(|_| NativeRecoveryError::Unavailable)?;
    if bytes.len() > 262_144 {
        return Err(NativeRecoveryError::Unavailable);
    }
    let phase = serde_json::to_string(&operation.phase)
        .map_err(|_| NativeRecoveryError::Unavailable)?
        .trim_matches('"')
        .to_owned();
    let checkpoint = checkpoint
        .map(Checkpoint::encode)
        .transpose()
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    if checkpoint.as_ref().is_some_and(|bytes| bytes.len() > 262_144) {
        return Err(NativeRecoveryError::Unavailable);
    }
    let changed = connection
        .execute(
            "UPDATE native_recovery_operations SET phase=?3, record=?4, checkpoint=?5 WHERE operation_id=?1 AND source_database_id=?2 AND (checkpoint IS NULL OR checkpoint=?5)",
            params![operation.operation_id, operation.source_database_id, phase, bytes, checkpoint],
        )
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    if changed != 1 {
        return Err(NativeRecoveryError::Unavailable);
    }
    Ok(())
}

pub(super) fn save_record_only(
    connection: &Connection,
    operation: &NativeRecoveryOperation,
) -> Result<(), NativeRecoveryError> {
    let bytes = serde_json::to_vec(operation).map_err(|_| NativeRecoveryError::Unavailable)?;
    if bytes.len() > 262_144 {
        return Err(NativeRecoveryError::Unavailable);
    }
    let phase = serde_json::to_string(&operation.phase)
        .map_err(|_| NativeRecoveryError::Unavailable)?
        .trim_matches('"')
        .to_owned();
    let changed = connection.execute(
        "UPDATE native_recovery_operations SET phase=?3, record=?4 WHERE operation_id=?1 AND source_database_id=?2",
        params![operation.operation_id, operation.source_database_id, phase, bytes],
    ).map_err(|_| NativeRecoveryError::Unavailable)?;
    if changed != 1 {
        return Err(NativeRecoveryError::Unavailable);
    }
    Ok(())
}

/// Replaces unfinished rows with a redacted terminal state after a bounded drain.
pub(super) fn fail_pending(connection: &Connection) -> Result<bool, NativeRecoveryError> {
    let mut statement = connection
        .prepare("SELECT operation_id, source_database_id FROM native_recovery_operations WHERE phase IN ('accepted','capturing','measuring')")
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    let rows = statement
        .query_map([], |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)))
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    let mut changed = false;
    for row in rows {
        let (operation_id, source_database_id) =
            row.map_err(|_| NativeRecoveryError::Unavailable)?;
        let operation = NativeRecoveryOperation {
            operation_id,
            source_database_id,
            phase: super::NativeRecoveryPhase::Failed,
            facts: None,
            error: Some("native recovery drain deadline reached; retry the existing operation".into()),
        };
        save_record_only(connection, &operation)?;
        changed = true;
    }
    Ok(changed)
}

pub(super) fn fail_row(
    connection: &Connection,
    operation_id: &str,
    source_database_id: &str,
    message: &str,
) -> Result<(), NativeRecoveryError> {
    let operation = NativeRecoveryOperation {
        operation_id: operation_id.to_owned(),
        source_database_id: source_database_id.to_owned(),
        phase: super::NativeRecoveryPhase::Failed,
        facts: None,
        error: Some(message.to_owned()),
    };
    save_record_only(connection, &operation)
}

#[cfg(test)]
mod tests;
