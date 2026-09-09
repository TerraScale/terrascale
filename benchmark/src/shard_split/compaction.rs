//! Bounded offline consolidation of SQLite shard files.
//!
//! Compaction is deliberately separate from split catch-up and cutover. The
//! caller must fence writers, copy the journal tail, and publish the route
//! only after the returned file has been validated. This module owns only the
//! durable temp-file build and atomic replacement.

use std::{
    fs,
    path::{Path, PathBuf},
};

use rusqlite::{Connection, TransactionBehavior};
use serde::{Deserialize, Serialize};

/// Restart marker for a consolidation attempt.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum CompactionPhase {
    /// Inputs were admitted and the manifest was created.
    Planned,
    /// Rows are being copied into the temporary database.
    Consolidating,
    /// The temporary database was synced and atomically installed.
    Installed,
}

/// Durable facts describing one consolidation attempt.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct CompactionManifest {
    /// Encoding version of this manifest.
    pub version: u8,
    /// Last durable phase reached by the attempt.
    pub phase: CompactionPhase,
    /// Input SQLite files consolidated, in copy order.
    pub sources: Vec<PathBuf>,
    /// Atomically installed output file.
    pub destination: PathBuf,
    /// Number of rows accepted by `INSERT OR IGNORE`.
    pub rows_inserted: u64,
}

impl CompactionManifest {
    /// Writes this manifest with a temporary-file rename and directory sync.
    ///
    /// Callers can persist the returned `Installed` manifest beside their
    /// route catalog and use it as the restart/rollback record for the
    /// consolidation operation.
    pub fn write_atomic(&self, path: &Path) -> Result<(), std::io::Error> {
        let temporary = path.with_extension("tmp");
        let bytes =
            serde_json::to_vec(self).map_err(|error| std::io::Error::other(error.to_string()))?;
        fs::write(&temporary, bytes)?;
        sync_file(&temporary).map_err(|error| match error {
            CompactionError::Io(error) => error,
            CompactionError::Sqlite(error) => std::io::Error::other(error.to_string()),
            CompactionError::InvalidInput => std::io::Error::other("invalid manifest path"),
        })?;
        fs::rename(&temporary, path)?;
        sync_directory(path.parent().unwrap_or_else(|| Path::new("."))).map_err(|error| match error
        {
            CompactionError::Io(error) => error,
            CompactionError::Sqlite(error) => std::io::Error::other(error.to_string()),
            CompactionError::InvalidInput => std::io::Error::other("invalid manifest path"),
        })
    }
}

/// Errors returned by SQLite consolidation.
#[derive(Debug, thiserror::Error)]
pub enum CompactionError {
    /// Inputs are empty, missing, or contain the destination.
    #[error("invalid compaction input")]
    InvalidInput,
    /// Filesystem operation failed.
    #[error("compaction I/O failed: {0}")]
    Io(std::io::Error),
    /// SQLite operation failed.
    #[error("compaction SQLite operation failed: {0}")]
    Sqlite(rusqlite::Error),
}

impl From<std::io::Error> for CompactionError {
    fn from(error: std::io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<rusqlite::Error> for CompactionError {
    fn from(error: rusqlite::Error) -> Self {
        Self::Sqlite(error)
    }
}

const MANIFEST_VERSION: u8 = 1;

fn identifier(value: &str) -> String {
    format!("\"{}\"", value.replace('"', "\"\""))
}

fn temporary_path(destination: &Path) -> PathBuf {
    let suffix = format!(
        "{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |duration| duration.as_nanos())
    );
    destination.with_extension(format!("compact-{suffix}.tmp"))
}

/// Consolidates one or more SQLite files into `destination`.
///
/// Every source is read through an attached connection, and each table is
/// copied with `INSERT OR IGNORE`, making retries idempotent for overlapping
/// shard ranges. The destination is built beside the target, synced, and
/// installed with rename; a failure leaves the prior destination untouched.
/// Catch-up and route cutover hooks belong to the caller around this function.
pub fn compact_sqlite_shards(
    sources: &[PathBuf],
    destination: &Path,
) -> Result<CompactionManifest, CompactionError> {
    if sources.is_empty() || destination.as_os_str().is_empty() {
        return Err(CompactionError::InvalidInput);
    }
    if sources.iter().any(|source| source == destination || !source.is_file()) {
        return Err(CompactionError::InvalidInput);
    }
    let temp = temporary_path(destination);
    let _ = fs::remove_file(&temp);
    let result = (|| {
        let mut output = Connection::open(&temp)?;
        output.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;")?;
        let tx = output.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let mut rows_inserted = 0u64;
        let mut aliases = Vec::with_capacity(sources.len());
        for (index, source) in sources.iter().enumerate() {
            let alias = format!("source_{index}");
            aliases.push(alias.clone());
            tx.execute_batch(&format!(
                "ATTACH DATABASE {} AS {}",
                quote_path(source),
                identifier(&alias)
            ))?;
            let mut objects = tx.prepare(&format!(
                "SELECT type, name, sql FROM {}.sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type DESC, name",
                identifier(&alias)
            ))?;
            let definitions = objects
                .query_map([], |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, String>(2)?,
                    ))
                })?
                .collect::<Result<Vec<_>, _>>()?;
            drop(objects);
            for (kind, _name, sql) in definitions.iter().filter(|(kind, _, _)| kind == "table") {
                let _ = kind;
                execute_schema(&tx, sql)?;
            }
            for (kind, name, sql) in definitions.iter().filter(|(kind, _, _)| kind != "table") {
                execute_schema(&tx, sql)?;
                let _ = (kind, name);
            }
            let mut tables = tx.prepare(&format!(
                "SELECT name FROM {}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'",
                identifier(&alias)
            ))?;
            let names = tables
                .query_map([], |row| row.get::<_, String>(0))?
                .collect::<Result<Vec<_>, _>>()?;
            drop(tables);
            for table in names {
                let sql = format!(
                    "INSERT OR IGNORE INTO {} SELECT * FROM {}.{}",
                    identifier(&table),
                    identifier(&alias),
                    identifier(&table)
                );
                rows_inserted = rows_inserted.saturating_add(tx.execute(&sql, [])? as u64);
            }
        }
        tx.commit()?;
        for alias in aliases {
            output.execute_batch(&format!("DETACH DATABASE {}", identifier(&alias)))?;
        }
        output.execute_batch("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA synchronous=FULL;")?;
        output.close().map_err(|(_, error)| error)?;
        sync_file(&temp)?;
        fs::rename(&temp, destination)?;
        sync_directory(destination.parent().unwrap_or_else(|| Path::new(".")))?;
        Ok(CompactionManifest {
            version: MANIFEST_VERSION,
            phase: CompactionPhase::Installed,
            sources: sources.to_vec(),
            destination: destination.to_owned(),
            rows_inserted,
        })
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temp);
    }
    result
}

/// Runs [`compact_sqlite_shards`] on Tokio's blocking pool so compaction never
/// occupies an async scheduler worker. The same atomic installation and
/// failure cleanup guarantees apply to this wrapper.
pub async fn compact_sqlite_shards_async(
    sources: Vec<PathBuf>,
    destination: PathBuf,
) -> Result<CompactionManifest, CompactionError> {
    tokio::task::spawn_blocking(move || compact_sqlite_shards(&sources, &destination))
        .await
        .map_err(|_| CompactionError::InvalidInput)?
}

fn quote_path(path: &Path) -> String {
    format!("'{}'", path.to_string_lossy().replace('\'', "''"))
}

fn execute_schema(tx: &rusqlite::Transaction<'_>, sql: &str) -> Result<(), CompactionError> {
    match tx.execute_batch(sql) {
        Ok(()) => Ok(()),
        Err(error) if error.to_string().contains("already exists") => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn sync_file(path: &Path) -> Result<(), CompactionError> {
    fs::File::open(path)?.sync_all()?;
    Ok(())
}

fn sync_directory(path: &Path) -> Result<(), CompactionError> {
    fs::File::open(path)?.sync_all()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn consolidates_rows_and_replaces_destination_atomically() {
        let root =
            std::env::temp_dir().join(format!("terrabase-compaction-{}", std::process::id()));
        let _ = fs::create_dir_all(&root);
        let a = root.join("a.sqlite");
        let b = root.join("b.sqlite");
        let out = root.join("out.sqlite");
        for (path, rows) in [(&a, [(1, "one"), (2, "two")]), (&b, [(2, "two"), (3, "three")])] {
            let db = Connection::open(path).unwrap();
            db.execute_batch("CREATE TABLE records (id INTEGER PRIMARY KEY, value TEXT NOT NULL);")
                .unwrap();
            for (id, value) in rows {
                db.execute("INSERT INTO records VALUES (?1, ?2)", (id, value)).unwrap();
            }
        }
        let manifest = compact_sqlite_shards(&[a.clone(), b.clone()], &out).unwrap();
        assert_eq!(manifest.phase, CompactionPhase::Installed);
        assert_eq!(manifest.rows_inserted, 3);
        let db = Connection::open(&out).unwrap();
        assert_eq!(
            db.query_row("SELECT COUNT(*) FROM records", [], |row| row.get::<_, i64>(0)).unwrap(),
            3
        );
        let _ = fs::remove_dir_all(root);
    }
}
