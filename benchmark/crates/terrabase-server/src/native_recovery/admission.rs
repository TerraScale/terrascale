use super::{NativeRecoveryError, NativeRecoveryLimits, NativeRecoveryOperation, NativeRecoveryPhase};
use rusqlite::{Connection, OptionalExtension, params};

const MAX_RECORD_BYTES: i64 = 262_144;

pub(crate) fn admit_operation(
    connection: &mut Connection,
    limits: NativeRecoveryLimits,
    proposed: NativeRecoveryOperation,
) -> Result<NativeRecoveryOperation, NativeRecoveryError> {
    let bytes = serde_json::to_vec(&proposed).map_err(|_| NativeRecoveryError::Unavailable)?;
    if bytes.len() > MAX_RECORD_BYTES as usize || proposed.source_database_id.is_empty() || proposed.operation_id.is_empty() {
        return Err(NativeRecoveryError::InvalidRequest);
    }
    let tx = connection.transaction().map_err(|_| NativeRecoveryError::Unavailable)?;
    let existing = tx.query_row(
        "SELECT source_database_id, phase, length(record), record FROM native_recovery_operations WHERE operation_id=?1",
        params![proposed.operation_id],
        |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?, row.get::<_, i64>(2)?, row.get::<_, Vec<u8>>(3)?)),
    ).optional().map_err(|_| NativeRecoveryError::Unavailable)?;
    if let Some((source, phase, size, stored)) = existing {
        if source != proposed.source_database_id || !(0..=MAX_RECORD_BYTES).contains(&size) {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let state: NativeRecoveryOperation = serde_json::from_slice(&stored).map_err(|_| NativeRecoveryError::VerificationFailed)?;
        let expected_phase = serde_json::to_string(&state.phase).map_err(|_| NativeRecoveryError::VerificationFailed)?.trim_matches('"').to_owned();
        if state.operation_id != proposed.operation_id || state.source_database_id != source || phase != expected_phase || (matches!(state.phase, NativeRecoveryPhase::Complete) && (state.facts.is_none() || state.error.is_some())) || (!matches!(state.phase, NativeRecoveryPhase::Complete) && state.facts.is_some()) {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        if matches!(state.phase, NativeRecoveryPhase::Failed) {
            let pending: i64 = tx.query_row("SELECT count(*) FROM native_recovery_operations WHERE phase IN ('accepted','capturing','measuring')", [], |row| row.get(0)).map_err(|_| NativeRecoveryError::Unavailable)?;
            if pending >= limits.pending_operations as i64 { return Err(NativeRecoveryError::Unavailable); }
            tx.execute("UPDATE native_recovery_operations SET phase='accepted', record=?2 WHERE operation_id=?1", params![proposed.operation_id, bytes]).map_err(|_| NativeRecoveryError::Unavailable)?;
            tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
            return Ok(proposed);
        }
        tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
        return Ok(state);
    }
    let pending: i64 = tx.query_row("SELECT count(*) FROM native_recovery_operations WHERE phase IN ('accepted','capturing','measuring')", [], |row| row.get(0)).map_err(|_| NativeRecoveryError::Unavailable)?;
    if pending >= limits.pending_operations as i64 { return Err(NativeRecoveryError::Unavailable); }
    let total: i64 = tx.query_row("SELECT count(*) FROM native_recovery_operations", [], |row| row.get(0)).map_err(|_| NativeRecoveryError::Unavailable)?;
    if total >= limits.retained_operations as i64 {
        tx.execute("DELETE FROM native_recovery_operations WHERE operation_id=(SELECT operation_id FROM native_recovery_operations WHERE phase IN ('complete','failed') ORDER BY operation_id LIMIT 1)", []).map_err(|_| NativeRecoveryError::Unavailable)?;
    }
    let total_after: i64 = tx.query_row("SELECT count(*) FROM native_recovery_operations", [], |row| row.get(0)).map_err(|_| NativeRecoveryError::Unavailable)?;
    if total_after >= limits.retained_operations as i64 { return Err(NativeRecoveryError::Unavailable); }
    tx.execute("INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,'accepted',?3)", params![proposed.operation_id, proposed.source_database_id, bytes]).map_err(|_| NativeRecoveryError::Unavailable)?;
    tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
    Ok(proposed)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn db() -> Connection {
        let connection = Connection::open_in_memory().unwrap();
        connection.execute_batch("CREATE TABLE native_recovery_operations(operation_id TEXT PRIMARY KEY, source_database_id TEXT NOT NULL, phase TEXT NOT NULL, record BLOB NOT NULL CHECK(length(record)<=262144));").unwrap();
        connection
    }
    fn op(id: &str, source: &str, phase: NativeRecoveryPhase) -> NativeRecoveryOperation {
        NativeRecoveryOperation { operation_id: id.into(), source_database_id: source.into(), phase, facts: None, error: None }
    }
    fn limits(pending: usize, total: usize) -> NativeRecoveryLimits { NativeRecoveryLimits { pending_operations: pending, retained_operations: total } }
    #[test]
    fn pending_full_rejects_without_mutation() {
        let mut c = db();
        admit_operation(&mut c, limits(1, 3), op("a", "db", NativeRecoveryPhase::Accepted)).unwrap();
        assert_eq!(admit_operation(&mut c, limits(1, 3), op("b", "db", NativeRecoveryPhase::Accepted)), Err(NativeRecoveryError::Unavailable));
        assert_eq!(c.query_row("SELECT count(*) FROM native_recovery_operations", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
    }
    #[test]
    fn total_bound_prunes_one_terminal_only() {
        let mut c = db();
        admit_operation(&mut c, limits(2, 2), op("a", "db", NativeRecoveryPhase::Complete)).ok();
        c.execute("UPDATE native_recovery_operations SET record=?2 WHERE operation_id=?1", params!["a", serde_json::to_vec(&NativeRecoveryOperation { facts: Some(serde_json::from_str("{\"recovery_point_id\":\"r\",\"source_database_id\":\"db\",\"journal_cut\":0,\"captured_at_millis\":0,\"checksum\":\"x\",\"row_count\":0,\"placement\":[],\"retained_until_millis\":0}").unwrap()), ..op("a", "db", NativeRecoveryPhase::Complete) }).unwrap()]).unwrap();
        admit_operation(&mut c, limits(2, 2), op("b", "db", NativeRecoveryPhase::Accepted)).unwrap();
        assert_eq!(c.query_row("SELECT count(*) FROM native_recovery_operations", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
    }
}
