//! Webhook control state in the admitted member's existing SQLite file.

use rusqlite::{OptionalExtension, params};

use super::SqliteStorage;
use crate::types::TenantId;

const MAX_RECORD_BYTES: usize = 1024 * 1024;
const MAX_NAMESPACE_BYTES: usize = 64 * 1024 * 1024;
const MAX_NAMESPACE_ROWS: usize = 100_000;

impl SqliteStorage {
    pub(super) fn load_webhook_metadata(
        &self,
        tenant: &TenantId,
        namespace: &str,
        key: &str,
    ) -> Result<Option<Vec<u8>>, ()> {
        let reader = self.reader();
        reader.connection().query_row(
            "SELECT record FROM webhook_metadata WHERE tenant = ?1 AND namespace = ?2 AND record_key = ?3",
            params![tenant.as_str(), namespace, key],
            |row| row.get(0),
        ).optional().map_err(|_| ())
    }

    pub(super) fn list_webhook_metadata(
        &self,
        tenant: &TenantId,
        namespace: &str,
    ) -> Result<Vec<(String, Vec<u8>)>, ()> {
        let reader = self.reader();
        let mut statement = reader.connection().prepare(
            "SELECT record_key, record FROM webhook_metadata WHERE tenant = ?1 AND namespace = ?2 ORDER BY record_key LIMIT ?3"
        ).map_err(|_| ())?;
        let mut rows = statement
            .query(params![
                tenant.as_str(),
                namespace,
                i64::try_from(MAX_NAMESPACE_ROWS + 1).map_err(|_| ())?
            ])
            .map_err(|_| ())?;
        let mut records = Vec::new();
        let mut bytes = 0_usize;
        while let Some(row) = rows.next().map_err(|_| ())? {
            if records.len() == MAX_NAMESPACE_ROWS {
                return Err(());
            }
            let key: String = row.get(0).map_err(|_| ())?;
            let record: Vec<u8> = row.get(1).map_err(|_| ())?;
            bytes = bytes
                .checked_add(key.len())
                .and_then(|total| total.checked_add(record.len()))
                .ok_or(())?;
            if bytes > MAX_NAMESPACE_BYTES {
                return Err(());
            }
            records.push((key, record));
        }
        Ok(records)
    }

    pub(super) fn compare_exchange_webhook_metadata(
        &self,
        tenant: &TenantId,
        namespace: &str,
        key: &str,
        expected: Option<&[u8]>,
        replacement: &[u8],
    ) -> Result<bool, ()> {
        if replacement.len() > MAX_RECORD_BYTES || key.len() > 4096 || namespace.len() > 256 {
            return Err(());
        }
        let writer = self.writer.lock().map_err(|_| ())?;
        let changed = match expected {
            None => writer.execute(
                "INSERT INTO webhook_metadata (tenant, namespace, record_key, record) VALUES (?1, ?2, ?3, ?4) ON CONFLICT DO NOTHING",
                params![tenant.as_str(), namespace, key, replacement],
            ),
            Some(previous) => writer.execute(
                "UPDATE webhook_metadata SET record = ?4 WHERE tenant = ?1 AND namespace = ?2 AND record_key = ?3 AND record = ?5",
                params![tenant.as_str(), namespace, key, replacement, previous],
            ),
        }.map_err(|_| ())?;
        Ok(changed == 1)
    }
}
