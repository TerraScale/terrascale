//! Durable `SQLite` export jobs with independent cancellation and OS job leases.

use std::{path::PathBuf, sync::Mutex, time::Duration};

use rusqlite::{Connection, OptionalExtension, params};

use crate::export_api::{
    ExportCancellation, ExportError, ExportJobLease, ExportJobState, ExportStatus, ExportStore,
    FileExportStore, JobStatus, status_from_state,
};

const MAX_STATE_BYTES: usize = 32 * 1024 * 1024;
const MAX_PROGRESS_BYTES: usize = 256 * 1024;
/// Export control records and staged artifacts are temporary operation data.
pub(crate) const EXPORT_RETENTION_MILLIS: u64 = 7 * 24 * 60 * 60 * 1000;

fn now_millis() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |value| value.as_millis().min(i64::MAX as u128) as i64)
}

/// `SQLite` control records and cancellation flags for resumable exports.
/// Job leases use persistent lock files, released by the OS on process exit.
pub struct SqliteExportStore {
    connection: Mutex<Connection>,
    leases: FileExportStore,
}

impl SqliteExportStore {
    /// Opens `exports.sqlite` and its lease directory beneath a trusted root.
    ///
    /// # Errors
    /// Refuses symlinks, unavailable storage, and failed schema initialization.
    pub fn open(root: impl Into<PathBuf>) -> Result<Self, ExportError> {
        let root = root.into();
        let leases = FileExportStore::open(root.join("leases"))?;
        let path = root.join("exports.sqlite");
        for suffix in ["", "-wal", "-shm"] {
            let path = root.join(format!("exports.sqlite{suffix}"));
            match std::fs::symlink_metadata(path) {
                Ok(metadata) if !metadata.is_file() => {
                    return Err(ExportError::DestinationUnavailable);
                }
                Ok(_) => {}
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                Err(error) => return Err(io_error(error)),
            }
        }
        let connection = Connection::open(path).map_err(io_error)?;
        connection.busy_timeout(Duration::from_secs(5)).map_err(io_error)?;
        connection
            .execute_batch(
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
             CREATE TABLE IF NOT EXISTS export_jobs (
                 job_id TEXT PRIMARY KEY NOT NULL,
                 state BLOB NOT NULL CHECK(length(state) <= 33554432),
                 progress BLOB NOT NULL CHECK(length(progress) <= 262144),
                 cancel_requested INTEGER NOT NULL DEFAULT 0 CHECK(cancel_requested IN (0,1)),
                 created_at INTEGER NOT NULL DEFAULT 0
             ) WITHOUT ROWID;",
            )
            .map_err(io_error)?;
        for ancestor in root.ancestors() {
            if !ancestor.as_os_str().is_empty() {
                std::fs::File::open(ancestor).and_then(|file| file.sync_all()).map_err(io_error)?;
            }
        }
        Ok(Self { connection: Mutex::new(connection), leases })
    }

    /// Reads a bounded indexed page from an existing journal without initializing
    /// storage, loading snapshots, or acquiring worker ownership.
    pub(crate) fn discover(
        root: &std::path::Path,
        after: Option<&str>,
        limit: usize,
    ) -> Result<Vec<ExportStatus>, ExportError> {
        if !(1..=51).contains(&limit) {
            return Err(ExportError::InvalidRequest("invalid discovery page"));
        }
        if let Some(after) = after {
            validate_id(after)?;
        }
        let path = root.join("exports.sqlite");
        for (candidate, directory) in [(root.to_path_buf(), true), (path.clone(), false)] {
            match std::fs::symlink_metadata(candidate) {
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
                Err(error) => return Err(io_error(error)),
                Ok(metadata)
                    if (directory && !metadata.is_dir()) || (!directory && !metadata.is_file()) =>
                {
                    return Err(ExportError::DestinationUnavailable);
                }
                Ok(_) => {}
            }
        }
        let connection = Connection::open_with_flags(
            path,
            rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY | rusqlite::OpenFlags::SQLITE_OPEN_NO_MUTEX,
        )
        .map_err(io_error)?;
        connection.busy_timeout(Duration::from_secs(5)).map_err(io_error)?;
        let mut statement = connection.prepare("SELECT job_id,length(progress),progress,cancel_requested FROM export_jobs WHERE job_id > ?1 ORDER BY job_id LIMIT ?2").map_err(io_error)?;
        let mut rows = statement
            .query(params![after.unwrap_or(""), i64::try_from(limit).map_err(io_error)?])
            .map_err(io_error)?;
        let mut result = Vec::new();
        while let Some(row) = rows.next().map_err(io_error)? {
            let id: String = row.get(0).map_err(io_error)?;
            validate_id(&id)?;
            let length: i64 = row.get(1).map_err(io_error)?;
            if length < 0
                || usize::try_from(length).map_or(true, |length| length > MAX_PROGRESS_BYTES)
            {
                return Err(ExportError::DestinationUnavailable);
            }
            let bytes: Vec<u8> = row.get(2).map_err(io_error)?;
            let requested: bool = row.get(3).map_err(io_error)?;
            let mut progress: ExportStatus = serde_json::from_slice(&bytes).map_err(io_error)?;
            if progress.job_id != id {
                return Err(ExportError::DestinationUnavailable);
            }
            progress.cancellation = match progress.status {
                JobStatus::Complete => ExportCancellation::Unavailable,
                JobStatus::Cancelled => ExportCancellation::Complete,
                _ if requested => ExportCancellation::Requested,
                _ => ExportCancellation::Available,
            };
            result.push(progress);
        }
        Ok(result)
    }

    fn update_cancel(&self, job_id: &str, requested: bool) -> Result<(), ExportError> {
        validate_id(job_id)?;
        let connection = self.connection.lock().map_err(io_error)?;
        let changed = connection
            .execute(
                "UPDATE export_jobs SET cancel_requested = ?2 WHERE job_id = ?1",
                params![job_id, requested],
            )
            .map_err(io_error)?;
        if changed != 1 {
            return Err(ExportError::InvalidRequest("export job was not found"));
        }
        Ok(())
    }
}

impl ExportStore for SqliteExportStore {
    fn acquire_job(&self, job_id: &str) -> Result<Option<Box<dyn ExportJobLease>>, ExportError> {
        self.leases.acquire_job(job_id)
    }

    fn create(&self, state: &ExportJobState) -> Result<ExportJobState, ExportError> {
        validate_id(&state.job_id)?;
        let bytes = encode(state)?;
        let progress = encode_progress(state)?;
        let mut connection = self.connection.lock().map_err(io_error)?;
        let transaction = connection
            .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)
            .map_err(io_error)?;
        transaction.execute("INSERT INTO export_jobs(job_id,state,progress,created_at) VALUES (?1,?2,?3,?4) ON CONFLICT(job_id) DO NOTHING", params![state.job_id, bytes, progress, now_millis()]).map_err(io_error)?;
        let stored: Vec<u8> = transaction
            .query_row(
                "SELECT length(state),state FROM export_jobs WHERE job_id=?1",
                [&state.job_id],
                bounded_state_bytes,
            )
            .map_err(io_error)?;
        let result = decode(&state.job_id, &stored)?;
        transaction.commit().map_err(io_error)?;
        Ok(result)
    }

    fn load(&self, job_id: &str) -> Result<Option<ExportJobState>, ExportError> {
        validate_id(job_id)?;
        let connection = self.connection.lock().map_err(io_error)?;
        let bytes: Option<Vec<u8>> = connection
            .query_row(
                "SELECT length(state),state FROM export_jobs WHERE job_id=?1",
                [job_id],
                bounded_state_bytes,
            )
            .optional()
            .map_err(io_error)?;
        bytes.map(|bytes| decode(job_id, &bytes)).transpose()
    }

    fn save(&self, state: &ExportJobState) -> Result<(), ExportError> {
        validate_id(&state.job_id)?;
        let bytes = encode(state)?;
        let progress = encode_progress(state)?;
        let connection = self.connection.lock().map_err(io_error)?;
        let changed = connection
            .execute(
                "UPDATE export_jobs SET state=?2,progress=?3 WHERE job_id=?1",
                params![state.job_id, bytes, progress],
            )
            .map_err(io_error)?;
        if changed != 1 {
            return Err(ExportError::InvalidRequest("export job was not found"));
        }
        Ok(())
    }

    fn progress(&self, job_id: &str) -> Result<Option<ExportStatus>, ExportError> {
        validate_id(job_id)?;
        let connection = self.connection.lock().map_err(io_error)?;
        let stored: Option<(Vec<u8>, bool)> = connection.query_row(
            "SELECT length(progress),progress,cancel_requested FROM export_jobs WHERE job_id=?1", [job_id],
            |row| {
                let length = row.get::<_, i64>(0)?;
                if length < 0 || usize::try_from(length).map_or(true, |length| length > MAX_PROGRESS_BYTES) { return Err(rusqlite::Error::InvalidQuery); }
                Ok((row.get(1)?, row.get(2)?))
            },
        ).optional().map_err(io_error)?;
        stored
            .map(|(bytes, requested)| {
                let mut progress: ExportStatus =
                    serde_json::from_slice(&bytes).map_err(io_error)?;
                if progress.job_id != job_id {
                    return Err(ExportError::InvalidRequest("export progress identity mismatch"));
                }
                progress.cancellation = match progress.status {
                    JobStatus::Complete => ExportCancellation::Unavailable,
                    JobStatus::Cancelled => ExportCancellation::Complete,
                    _ if requested => ExportCancellation::Requested,
                    _ => ExportCancellation::Available,
                };
                Ok(progress)
            })
            .transpose()
    }

    fn list(&self, after: Option<&str>, limit: usize) -> Result<Vec<String>, ExportError> {
        if let Some(after) = after {
            validate_id(after)?;
        }
        if limit > 100 {
            return Err(ExportError::InvalidRequest("export list limit exceeds 100"));
        }
        let connection = self.connection.lock().map_err(io_error)?;
        let mut statement = connection.prepare("SELECT length(job_id),job_id FROM export_jobs WHERE job_id > ?1 ORDER BY job_id LIMIT ?2").map_err(io_error)?;
        let mut rows = statement
            .query(params![after.unwrap_or(""), i64::try_from(limit).map_err(io_error)?])
            .map_err(io_error)?;
        let mut result = Vec::new();
        while let Some(row) = rows.next().map_err(io_error)? {
            if row.get::<_, i64>(0).map_err(io_error)? != 32 {
                return Err(ExportError::InvalidRequest("job identity is invalid"));
            }
            let id: String = row.get(1).map_err(io_error)?;
            validate_id(&id)?;
            result.push(id);
        }
        Ok(result)
    }

    fn request_cancel(&self, job_id: &str) -> Result<bool, ExportError> {
        validate_id(job_id)?;
        let connection = self.connection.lock().map_err(io_error)?;
        connection.execute(
            "UPDATE export_jobs SET cancel_requested=1 WHERE job_id=?1 AND json_extract(CAST(progress AS TEXT), '$.status') NOT IN ('Complete','Cancelled')",
            [job_id],
        ).map(|changed| changed == 1).map_err(io_error)
    }

    fn cancel_requested(&self, job_id: &str) -> Result<bool, ExportError> {
        validate_id(job_id)?;
        self.connection
            .lock()
            .map_err(io_error)?
            .query_row(
                "SELECT cancel_requested FROM export_jobs WHERE job_id=?1",
                [job_id],
                |row| row.get(0),
            )
            .optional()
            .map_err(io_error)
            .map(|requested| requested.unwrap_or(false))
    }

    fn clear_cancel(&self, job_id: &str) -> Result<(), ExportError> {
        self.update_cancel(job_id, false)
    }
}

impl SqliteExportStore {
    /// Deletes only terminal export records after their durable retention
    /// window. Active or failed-resumable jobs remain available to recovery.
    /// The returned IDs let the runtime remove local artifact directories
    /// owned by this database without exposing paths through the store API.
    pub(crate) fn purge_expired(&self, now_millis: u64) -> Result<Vec<String>, ExportError> {
        let cutoff = now_millis.saturating_sub(EXPORT_RETENTION_MILLIS) as i64;
        let connection = self.connection.lock().map_err(io_error)?;
        let mut statement = connection
            .prepare("SELECT job_id FROM export_jobs WHERE created_at > 0 AND created_at <= ?1 AND json_extract(CAST(progress AS TEXT), '$.status') IN ('Complete','Cancelled','Failed')")
            .map_err(io_error)?;
        let ids = statement
            .query_map([cutoff], |row| row.get::<_, String>(0))
            .map_err(io_error)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(io_error)?;
        drop(statement);
        let tx = connection.unchecked_transaction().map_err(io_error)?;
        for id in &ids {
            tx.execute("DELETE FROM export_jobs WHERE job_id=?1", [id]).map_err(io_error)?;
        }
        tx.commit().map_err(io_error)?;
        Ok(ids)
    }
}

fn validate_id(id: &str) -> Result<(), ExportError> {
    if id.len() != 32 || !id.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(ExportError::InvalidRequest("job identity is invalid"));
    }
    Ok(())
}

fn encode(state: &ExportJobState) -> Result<Vec<u8>, ExportError> {
    let mut bytes = BoundedStateBytes::<MAX_STATE_BYTES>(Vec::new());
    serde_json::to_writer(&mut bytes, state).map_err(io_error)?;
    Ok(bytes.0)
}

struct BoundedStateBytes<const LIMIT: usize>(Vec<u8>);

impl<const LIMIT: usize> std::io::Write for BoundedStateBytes<LIMIT> {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        if bytes.len() > LIMIT.saturating_sub(self.0.len()) {
            return Err(std::io::Error::other("export state exceeds byte limit"));
        }
        self.0.extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

fn decode(id: &str, bytes: &[u8]) -> Result<ExportJobState, ExportError> {
    if bytes.len() > MAX_STATE_BYTES {
        return Err(ExportError::InvalidRequest("export state exceeds byte limit"));
    }
    let state: ExportJobState = serde_json::from_slice(bytes).map_err(io_error)?;
    if state.job_id != id {
        return Err(ExportError::InvalidRequest("export state identity mismatch"));
    }
    Ok(state)
}

fn io_error(error: impl std::fmt::Display) -> ExportError {
    ExportError::Io(error.to_string())
}

fn bounded_state_bytes(row: &rusqlite::Row<'_>) -> rusqlite::Result<Vec<u8>> {
    let length = row.get::<_, i64>(0)?;
    if length < 0 || usize::try_from(length).map_or(true, |length| length > MAX_STATE_BYTES) {
        return Err(rusqlite::Error::InvalidQuery);
    }
    row.get(1)
}

fn encode_progress(state: &ExportJobState) -> Result<Vec<u8>, ExportError> {
    let progress = status_from_state(state, ExportCancellation::Available)?;
    let mut bytes = BoundedStateBytes::<MAX_PROGRESS_BYTES>(Vec::new());
    serde_json::to_writer(&mut bytes, &progress).map_err(io_error)?;
    Ok(bytes.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::export_api::{ExportDestination, ExportPhase, ExportSnapshot, JobStatus};
    use terrabase::regions::RegionCode;

    fn state(id: char) -> ExportJobState {
        ExportJobState {
            format: crate::export_formats::TransferFormat::Native,
            scope: crate::export_formats::ExportScope::All,
            job_id: id.to_string().repeat(32),
            database: "db".into(),
            destination: ExportDestination::File {
                root: PathBuf::from("artifacts"),
                region: RegionCode::GRU,
            },
            placement: vec![RegionCode::GRU],
            snapshot: ExportSnapshot {
                captured_at_millis: 0,
                history_start: 0,
                boundary: 1,
                database_id: "db".into(),
                schemas: vec![],
                indexes: vec![],
                branches: vec![],
                estimated_records: None,
            },
            phase: ExportPhase::Documents,
            source_cursor: None,
            records: 0,
            pages: 0,
            status: JobStatus::Accepted,
            resume_cursor: "cursor".into(),
            manifest: None,
            error: None,
        }
    }

    #[test]
    fn durable_jobs_preserve_cancellation_progress_and_exclusive_ownership() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-sqlite-export-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let first = SqliteExportStore::open(&root).expect("store");
        let second = SqliteExportStore::open(&root).expect("second handle");
        let mut job = state('a');
        first.create(&job).expect("create");
        let lease = first.acquire_job(&job.job_id).expect("lease").expect("owned");
        assert!(second.acquire_job(&job.job_id).expect("contender").is_none());
        assert!(second.request_cancel(&job.job_id).expect("request"));
        job.pages = 3;
        first.save(&job).expect("progress");
        assert!(first.cancel_requested(&job.job_id).expect("cancel survives progress"));
        let progress = first.progress(&job.job_id).expect("small progress").expect("exists");
        assert_eq!(progress.pages, 3);
        assert_eq!(progress.cancellation, ExportCancellation::Requested);
        let mut replay = job.clone();
        replay.pages = 0;
        assert_eq!(second.create(&replay).expect("idempotent replay").pages, 3);
        drop(lease);
        assert!(second.acquire_job(&job.job_id).expect("lease released").is_some());
        first.create(&state('b')).expect("second job");
        let page = second.list(None, 1).expect("first page");
        assert_eq!(page[0], job.job_id);
        assert_eq!(second.list(Some(&job.job_id), 1).expect("next page")[0], "b".repeat(32));
        assert!(second.list(None, 101).is_err());
        drop(first);
        drop(second);
        let reopened = SqliteExportStore::open(&root).expect("reopen");
        assert!(reopened.cancel_requested(&job.job_id).expect("durable cancellation"));
        assert_eq!(reopened.load(&job.job_id).expect("load").expect("exists").pages, 3);
        reopened.clear_cancel(&job.job_id).expect("resume clears cancellation");
        assert!(!reopened.cancel_requested(&job.job_id).expect("cleared"));
        job.status = JobStatus::Complete;
        reopened.save(&job).expect("complete");
        assert!(!reopened.request_cancel(&job.job_id).expect("terminal cancel"));
        drop(reopened);
        std::fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn progress_ignores_corrupt_and_oversized_snapshot_state() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-sqlite-export-progress-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let store = SqliteExportStore::open(&root).expect("store");
        let job = state('d');
        store.create(&job).expect("create");
        {
            let connection = store.connection.lock().expect("connection");
            connection.execute("UPDATE export_jobs SET state=x'00'", []).expect("corrupt metadata");
        }
        assert!(store.load(&job.job_id).is_err());
        assert_eq!(store.progress(&job.job_id).expect("progress").expect("exists").pages, 0);
        {
            let connection = store.connection.lock().expect("connection");
            connection.execute_batch("PRAGMA ignore_check_constraints=ON; UPDATE export_jobs SET state=zeroblob(33554433); PRAGMA ignore_check_constraints=OFF;").expect("oversized metadata fixture");
        }
        assert!(store.load(&job.job_id).is_err());
        assert!(store.request_cancel(&job.job_id).expect("cancel without reading metadata"));
        assert_eq!(
            store.progress(&job.job_id).expect("progress").expect("exists").cancellation,
            ExportCancellation::Requested
        );
        drop(store);
        std::fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn competing_admission_keeps_one_snapshot() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-sqlite-export-race-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let first = SqliteExportStore::open(&root).expect("first store");
        let second = SqliteExportStore::open(&root).expect("second store");
        let barrier = std::sync::Barrier::new(2);
        let boundaries = std::thread::scope(|threads| {
            let left = threads.spawn(|| {
                let mut job = state('c');
                job.snapshot.boundary = 10;
                barrier.wait();
                first.create(&job).expect("first admission").snapshot.boundary
            });
            let right = threads.spawn(|| {
                let mut job = state('c');
                job.snapshot.boundary = 20;
                barrier.wait();
                second.create(&job).expect("second admission").snapshot.boundary
            });
            (left.join().expect("first thread"), right.join().expect("second thread"))
        });
        assert_eq!(boundaries.0, boundaries.1);
        drop(first);
        drop(second);
        std::fs::remove_dir_all(root).expect("cleanup");
    }
}
