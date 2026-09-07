//! Durable metadata for authenticated import input sessions.
//!
//! This owner is intentionally not wired into HTTP yet.  `input_digest` and
//! `input_length` describe encrypted staged bytes; callers must use an
//! import-purpose `ArtifactCrypto` lease before invoking `commit_input`.

use rusqlite::{Connection, OptionalExtension, params};
use std::{path::Path, sync::Mutex, time::Duration};

const MAX_INPUT_BYTES: u64 = 1 << 30;

#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct ImportJob {
    pub operation_id: String,
    pub tenant_id: String,
    pub project_id: String,
    pub database_id: String,
    pub format: String,
    pub input_digest: Option<String>,
    pub input_length: Option<u64>,
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
               database_id TEXT NOT NULL, format TEXT NOT NULL,
               input_digest TEXT, input_length INTEGER,
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
            "INSERT INTO import_jobs(operation_id,tenant_id,project_id,database_id,format,state)
             VALUES(?1,?2,?3,?4,?5,?6)
             ON CONFLICT(operation_id) DO NOTHING",
            params![job.operation_id, job.tenant_id, job.project_id, job.database_id, job.format, job.state],
        )?;
        Ok(())
    }

    pub(crate) fn get(&self, operation_id: &str) -> rusqlite::Result<Option<ImportJob>> {
        let connection = self.connection.lock().expect("import store lock");
        connection.query_row(
            "SELECT operation_id,tenant_id,project_id,database_id,format,input_digest,input_length,
             input_committed,cursor,committed_records,state,error FROM import_jobs WHERE operation_id=?1",
            [operation_id],
            |row| Ok(ImportJob {
                operation_id: row.get(0)?, tenant_id: row.get(1)?, project_id: row.get(2)?,
                database_id: row.get(3)?, format: row.get(4)?, input_digest: row.get(5)?,
                input_length: row.get::<_, Option<i64>>(6)?.map(|v| v as u64),
                input_committed: row.get::<_, i64>(7)? != 0, cursor: row.get::<_, i64>(8)? as u64,
                committed_records: row.get::<_, i64>(9)? as u64, state: row.get(10)?, error: row.get(11)?,
            }),
        ).optional()
    }

    pub(crate) fn commit_input(&self, operation_id: &str, digest: &str, length: u64) -> rusqlite::Result<()> {
        if length > MAX_INPUT_BYTES || digest.len() != 64 || !digest.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(rusqlite::Error::InvalidParameterName("invalid staged input".into()));
        }
        let connection = self.connection.lock().expect("import store lock");
        connection.execute(
            "UPDATE import_jobs SET input_digest=?2,input_length=?3,input_committed=1,state='queued'
             WHERE operation_id=?1 AND input_committed=0",
            params![operation_id, digest, i64::try_from(length).map_err(|_| rusqlite::Error::InvalidQuery)?],
        )?;
        Ok(())
    }
}
