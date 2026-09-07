//! Durable metadata for authenticated import input sessions.
//!
//! `input_digest` and
//! `input_length` describe encrypted staged bytes; callers must use an
//! import-purpose `ArtifactCrypto` lease before invoking `commit_input`.

use rusqlite::{Connection, OptionalExtension, params};
use std::{path::Path, sync::Mutex, time::Duration};

const MAX_INPUT_BYTES: u64 = 1 << 30;

#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize)]
pub(crate) struct ImportJob {
    pub operation_id: String,
    pub tenant_id: String,
    pub project_id: String,
    pub database_id: String,
    pub format: String,
    pub durability: String,
    pub pending_operation: Option<String>,
    pub pending_cursor: Option<u64>,
    pub pending_records: Option<u64>,
    pub input_digest: Option<String>,
    pub input_length: Option<u64>,
    #[serde(skip)]
    pub input_path: Option<String>,
    pub input_committed: bool,
    pub cursor: u64,
    pub committed_records: u64,
    pub state: String,
    pub error: Option<String>,
}

/// Separate durable import owner; it never reads or writes `export_jobs`.
pub(crate) struct ImportStore {
    connection: Mutex<Connection>,
}

impl ImportStore {
    pub(crate) fn open(root: impl AsRef<Path>) -> rusqlite::Result<Self> {
        let path = root.as_ref().join("imports.sqlite");
        let connection = Connection::open(path)?;
        connection.busy_timeout(Duration::from_secs(5))?;
        connection.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
             CREATE TABLE IF NOT EXISTS import_jobs(
               operation_id TEXT PRIMARY KEY NOT NULL,
               tenant_id TEXT NOT NULL, project_id TEXT NOT NULL,
               database_id TEXT NOT NULL, format TEXT NOT NULL, durability TEXT NOT NULL,
               pending_operation TEXT, pending_cursor INTEGER, pending_records INTEGER,
               input_digest TEXT, input_length INTEGER, input_path TEXT,
               input_committed INTEGER NOT NULL DEFAULT 0,
               cursor INTEGER NOT NULL DEFAULT 0,
               committed_records INTEGER NOT NULL DEFAULT 0,
               state TEXT NOT NULL, error TEXT
             ) WITHOUT ROWID;",
        )?;
        Ok(Self { connection: Mutex::new(connection) })
    }

    pub(crate) fn admit(&self, job: &ImportJob) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        connection.execute(
            "INSERT INTO import_jobs(operation_id,tenant_id,project_id,database_id,format,state,durability)
             VALUES(?1,?2,?3,?4,?5,?6,?7)
             ON CONFLICT(operation_id) DO NOTHING",
            params![
                job.operation_id,
                job.tenant_id,
                job.project_id,
                job.database_id,
                job.format,
                job.state, job.durability
            ],
        )?;
        Ok(())
    }

    pub(crate) fn get(&self, operation_id: &str) -> rusqlite::Result<Option<ImportJob>> {
        let connection = self.connection.lock().expect("import store lock");
        connection.query_row(
            "SELECT operation_id,tenant_id,project_id,database_id,format,input_digest,input_length,input_path,
             input_committed,cursor,committed_records,state,error,durability,pending_operation,pending_cursor,pending_records FROM import_jobs WHERE operation_id=?1",
            [operation_id],
            |row| Ok(ImportJob {
                operation_id: row.get(0)?, tenant_id: row.get(1)?, project_id: row.get(2)?,
                database_id: row.get(3)?, format: row.get(4)?, durability: row.get(13)?,
                pending_operation: row.get(14)?, pending_cursor: row.get::<_, Option<i64>>(15)?.map(|v| v as u64), pending_records: row.get::<_, Option<i64>>(16)?.map(|v| v as u64), input_digest: row.get(5)?,
                input_length: row.get::<_, Option<i64>>(6)?.map(|v| v as u64),
                input_path: row.get(7)?,
                input_committed: row.get::<_, i64>(8)? != 0, cursor: row.get::<_, i64>(9)? as u64,
                committed_records: row.get::<_, i64>(10)? as u64, state: row.get(11)?, error: row.get(12)?,
            }),
        ).optional()
    }

    pub(crate) fn commit_input(
        &self,
        operation_id: &str,
        digest: &str,
        length: u64,
        path: &Path,
    ) -> rusqlite::Result<()> {
        if length > MAX_INPUT_BYTES
            || digest.len() != 64
            || !digest.bytes().all(|b| b.is_ascii_hexdigit())
        {
            return Err(rusqlite::Error::InvalidParameterName("invalid staged input".into()));
        }
        let metadata = std::fs::symlink_metadata(path).map_err(|_| {
            rusqlite::Error::InvalidParameterName("staged input is unavailable".into())
        })?;
        if path.extension().and_then(|value| value.to_str()) != Some("upload")
            || !metadata.is_file()
            || (length == 0 && metadata.len() != 0)
            || (length != 0 && metadata.len() == 0)
        {
            return Err(rusqlite::Error::InvalidParameterName("staged input is incomplete".into()));
        }
        let connection = self.connection.lock().expect("import store lock");
        let changed = connection.execute(
            "UPDATE import_jobs SET input_digest=?2,input_length=?3,input_path=?4,input_committed=1,state='queued'
             WHERE operation_id=?1 AND input_committed=0",
            params![operation_id, digest, i64::try_from(length).map_err(|_| rusqlite::Error::InvalidQuery)?, path.to_string_lossy().as_ref()],
        )?;
        if changed != 1 {
            return Err(rusqlite::Error::InvalidParameterName(
                "import operation is missing or already committed".into(),
            ));
        }
        Ok(())
    }

    /// Advances the import cursor only after the corresponding engine batch
    /// has committed. The monotonic predicate makes replay after a crash
    /// idempotent and refuses a caller that attempts to move the checkpoint
    /// backwards or account more records than it has committed.
    pub(crate) fn checkpoint(
        &self,
        operation_id: &str,
        cursor: u64,
        committed_records: u64,
    ) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        let changed = connection.execute(
            "UPDATE import_jobs SET cursor=?2, committed_records=?3, state='running', pending_operation=NULL,pending_cursor=NULL,pending_records=NULL
             WHERE operation_id=?1 AND input_committed=1
               AND cursor<=?2 AND committed_records<=?3
               AND state IN ('queued','running','failed')",
            params![
                operation_id,
                i64::try_from(cursor).map_err(|_| rusqlite::Error::InvalidQuery)?,
                i64::try_from(committed_records).map_err(|_| rusqlite::Error::InvalidQuery)?,
            ],
        )?;
        if changed != 1 {
            return Err(rusqlite::Error::InvalidParameterName(
                "import checkpoint is stale or operation is unavailable".into(),
            ));
        }
        Ok(())
    }

    /// Records a terminal failure at the last committed cursor. This leaves
    /// the operation resumable and never advances billing/accounting state.
    pub(crate) fn fail(&self, operation_id: &str, error: &str) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        let changed = connection.execute(
            "UPDATE import_jobs SET state='failed', error=?2
             WHERE operation_id=?1 AND input_committed=1 AND state IN ('queued','running')",
            params![operation_id, error],
        )?;
        if changed != 1 {
            return Err(rusqlite::Error::InvalidParameterName(
                "import operation is unavailable".into(),
            ));
        }
        Ok(())
    }

    pub(crate) fn list(&self) -> rusqlite::Result<Vec<ImportJob>> {
        let ids = {
            let connection = self.connection.lock().expect("import store lock");
            let mut statement = connection.prepare("SELECT operation_id FROM import_jobs ORDER BY operation_id")?;
            statement.query_map([], |row| row.get::<_, String>(0))?.collect::<rusqlite::Result<Vec<_>>>()?
        };
        ids.iter().map(|id| self.get(id)?.ok_or(rusqlite::Error::InvalidQuery)).collect()
    }

    pub(crate) fn claim(&self, id: &str) -> rusqlite::Result<bool> {
        let connection = self.connection.lock().expect("import store lock");
        Ok(connection.execute("UPDATE import_jobs SET state='running',error=NULL WHERE operation_id=?1 AND input_committed=1 AND state IN ('queued','failed','unknown')", [id])? == 1)
    }

    pub(crate) fn begin_batch(&self, id: &str, operation: &str, cursor: u64, records: u64) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        if connection.execute("UPDATE import_jobs SET pending_operation=?2,pending_cursor=?3,pending_records=?4 WHERE operation_id=?1 AND state='running' AND pending_operation IS NULL", params![id, operation, cursor, records])? != 1 { return Err(rusqlite::Error::InvalidQuery); }
        Ok(())
    }

    pub(crate) fn hold(&self, id: &str) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        connection.execute("UPDATE import_jobs SET state='unknown',error='import commit outcome unknown' WHERE operation_id=?1 AND pending_operation IS NOT NULL", [id])?;
        Ok(())
    }

    pub(crate) fn clear_rejected(&self, id: &str) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        connection.execute("UPDATE import_jobs SET pending_operation=NULL,pending_cursor=NULL,pending_records=NULL WHERE operation_id=?1", [id])?;
        Ok(())
    }

    /// A process crash cannot prove whether a running batch landed.
    pub(crate) fn recover(&self) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        connection.execute("UPDATE import_jobs SET state=CASE WHEN pending_operation IS NULL THEN 'queued' ELSE 'unknown' END WHERE state='running'", [])?;
        Ok(())
    }

    /// Marks an import complete without changing its last proven cursor.
    pub(crate) fn complete(&self, operation_id: &str) -> rusqlite::Result<()> {
        let connection = self.connection.lock().expect("import store lock");
        let changed = connection.execute(
            "UPDATE import_jobs SET state='completed', error=NULL
             WHERE operation_id=?1 AND input_committed=1 AND state IN ('queued','running','failed')",
            [operation_id],
        )?;
        if changed != 1 {
            return Err(rusqlite::Error::InvalidParameterName(
                "import operation is unavailable".into(),
            ));
        }
        Ok(())
    }
}
