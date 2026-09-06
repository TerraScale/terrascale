use super::{Checkpoint, NativeRecoveryError, NativeRecoveryOperation, NativeRecoveryPhase};
use rusqlite::{params, Connection};

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
    let changed = connection
        .execute(
            "UPDATE native_recovery_operations SET phase=?3, record=?4, checkpoint=?5 WHERE operation_id=?1 AND source_database_id=?2",
            params![operation.operation_id, operation.source_database_id, phase, bytes, checkpoint],
        )
        .map_err(|_| NativeRecoveryError::Unavailable)?;
    if changed != 1 { return Err(NativeRecoveryError::Unavailable); }
    Ok(())
}

pub(super) fn save_record_only(
    connection: &Connection,
    operation: &NativeRecoveryOperation,
) -> Result<(), NativeRecoveryError> {
    let bytes = serde_json::to_vec(operation).map_err(|_| NativeRecoveryError::Unavailable)?;
    if bytes.len() > 262_144 { return Err(NativeRecoveryError::Unavailable); }
    let phase = serde_json::to_string(&operation.phase)
        .map_err(|_| NativeRecoveryError::Unavailable)?.trim_matches('"').to_owned();
    let changed = connection.execute(
        "UPDATE native_recovery_operations SET phase=?3, record=?4 WHERE operation_id=?1 AND source_database_id=?2",
        params![operation.operation_id, operation.source_database_id, phase, bytes],
    ).map_err(|_| NativeRecoveryError::Unavailable)?;
    if changed != 1 { return Err(NativeRecoveryError::Unavailable); }
    Ok(())
}
