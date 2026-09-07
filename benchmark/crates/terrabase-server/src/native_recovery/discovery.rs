//! Indexed, read-only lifecycle projection of the existing native journals.

use super::{NativeRecoveryError, NativeRecoveryRuntime};
use rusqlite::params;

pub(crate) struct RecoveryDiscovery {
    pub operation_id: String,
    pub phase: String,
}

impl NativeRecoveryRuntime {
    pub(crate) fn discover(
        &self,
        database: &str,
        after: &str,
        limit: usize,
        restore: bool,
    ) -> Result<Vec<RecoveryDiscovery>, NativeRecoveryError> {
        if !(1..=51).contains(&limit) || after.len() > 128 {
            return Err(NativeRecoveryError::InvalidRequest);
        }
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        let sql = if restore {
            "SELECT operation_id,phase FROM native_restore_operations WHERE source_database_id=?1 AND operation_id>?2 ORDER BY operation_id LIMIT ?3"
        } else {
            "SELECT operation_id,phase FROM native_recovery_operations WHERE source_database_id=?1 AND operation_id>?2 ORDER BY operation_id LIMIT ?3"
        };
        let mut statement =
            connection.prepare(sql).map_err(|_| NativeRecoveryError::Unavailable)?;
        let mut rows = statement
            .query(params![
                database,
                after,
                i64::try_from(limit).map_err(|_| NativeRecoveryError::InvalidRequest)?
            ])
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        let mut result = Vec::new();
        while let Some(row) = rows.next().map_err(|_| NativeRecoveryError::Unavailable)? {
            let operation_id: String = row.get(0).map_err(|_| NativeRecoveryError::Unavailable)?;
            let phase: String = row.get(1).map_err(|_| NativeRecoveryError::Unavailable)?;
            if operation_id.is_empty() || operation_id.len() > 128 || phase.len() > 32 {
                return Err(NativeRecoveryError::Unavailable);
            }
            result.push(RecoveryDiscovery { operation_id, phase });
        }
        Ok(result)
    }
}
