//! The embedded SQLite reference driver: raw SQLite, no TerraBase layer.
//!
//! The SQLite analogue of the SE-15 PostgreSQL pin: the bundled SQLite
//! library at WAL + `synchronous = FULL` + a five-second busy timeout on
//! every connection, so each acknowledged statement is durable the way the
//! PostgreSQL pin's `synchronous_commit = on` commits are; one table
//! `users(tenant, id, name, "group", payload)` with a primary key on
//! `(tenant, id)` and an index on `(tenant, name)` created before warm-up;
//! one connection per concurrency slot with cached prepared statements;
//! autocommit per statement. There is no server: the measured member is
//! this driver's own process, which the receipt states in `limits`.

use std::error::Error;
use std::path::PathBuf;
use std::time::Duration;

use rusqlite::{Connection, params};

use crate::config::LaneConfig;
use crate::lane::{Backend, Slot, document};

/// The environment variable that names the reference file. The default
/// lives under `bench/target/` so the database sits on the same real
/// filesystem as the engine's own runs, never on a tmpfs.
const PATH_VARIABLE: &str = "TERRABASE_SQLITE_REFERENCE_PATH";
const DEFAULT_PATH: &str = "bench/target/sqlite-reference.db";

/// The file this run opens, from the environment or the default.
fn reference_path() -> PathBuf {
    std::env::var(PATH_VARIABLE).map_or_else(|_| PathBuf::from(DEFAULT_PATH), PathBuf::from)
}

/// Opens one connection with the pin's pragmas.
fn open_pinned(path: &std::path::Path) -> Result<Connection, Box<dyn Error + Send + Sync>> {
    let connection = Connection::open(path)?;
    let journal_mode: String =
        connection.query_row("PRAGMA journal_mode=WAL", [], |row| row.get(0))?;
    if !journal_mode.eq_ignore_ascii_case("wal") {
        return Err(format!("journal_mode refused WAL: {journal_mode}").into());
    }
    connection.pragma_update(None, "synchronous", "FULL")?;
    connection.busy_timeout(Duration::from_millis(5_000))?;
    Ok(connection)
}

/// The administration connection: schema, seeding, and identity capture.
pub struct SqliteBackend {
    admin: Connection,
    writer: std::sync::Arc<std::sync::Mutex<Connection>>,
    path: PathBuf,
}

impl Backend for SqliteBackend {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let path = reference_path();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        // A fresh store per run, the pin's discipline: any prior run's data,
        // including its WAL and shared-memory siblings, is discarded.
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
        }
        let admin = open_pinned(&path)?;
        let writer = std::sync::Arc::new(std::sync::Mutex::new(open_pinned(&path)?));
        Ok(Self { admin, writer, path })
    }

    fn target_name(&self) -> &'static str {
        "sqlite"
    }

    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
        let version: String =
            self.admin.query_row("SELECT sqlite_version()", [], |row| row.get(0))?;
        Ok(format!(
            "SQLite {version} (bundled); journal_mode=WAL; synchronous=FULL; busy_timeout=5000ms; \
             one writer connection, per-slot concurrent readers"
        ))
    }

    fn endpoint(&self) -> String {
        format!("file://{}", self.path.display())
    }

    fn client_transport(&self) -> &'static str {
        "bundled rusqlite, one embedded reader connection per concurrency slot, writes \
         serialized through one shared writer connection, cached prepared statements, autocommit \
         per statement"
    }

    fn setup(&mut self, _config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // The same shape as the PostgreSQL pin's table and index.
        self.admin.execute_batch(
            "DROP TABLE IF EXISTS users;\n\
             CREATE TABLE users(\n\
                 tenant TEXT NOT NULL,\n\
                 id TEXT NOT NULL,\n\
                 name TEXT NOT NULL,\n\
                 \"group\" INTEGER NOT NULL,\n\
                 payload TEXT NOT NULL,\n\
                 PRIMARY KEY (tenant, id));\n\
             CREATE INDEX users_tenant_name_idx ON users (tenant, name);",
        )?;
        Ok(())
    }

    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        let payload = document::payload();
        for index in 0..config.seed_documents {
            self.admin.execute(
                "INSERT INTO users(tenant, id, name, \"group\", payload) \
                 VALUES (?1, ?2, ?3, ?4, ?5)",
                params![
                    document::TENANT,
                    document::seeded_id(index),
                    document::seeded_name(index),
                    document::seeded_group(index),
                    payload,
                ],
            )?;
        }
        Ok(())
    }

    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
        Ok(Box::new(SqliteSlot {
            reader: open_pinned(&self.path)?,
            writer: std::sync::Arc::clone(&self.writer),
        }))
    }

    fn limits(&self) -> Vec<String> {
        vec![
            "embedded reference: the measured member is this driver's own process, so the load \
             generator and the database share one resource envelope"
                .to_owned(),
            "SQLite's concurrency model is many readers, one writer: reads and queries run \
             concurrently on per-slot connections while every write serializes through one shared \
             writer connection, the canonical deployment shape"
                .to_owned(),
            "sixteen contending per-slot writers instead busy-retry the WAL write lock and a \
             starved slot can exceed the busy timeout; that retries SQLite's lock manager, not \
             its storage, so this reference does not drive it"
                .to_owned(),
        ]
    }
}

/// One concurrency slot: its own reader connection (WAL readers never block
/// writers) plus the shared writer connection. Statements are cached per
/// connection by `prepare_cached`, the embedded analogue of the pin's
/// prepared statements.
struct SqliteSlot {
    reader: Connection,
    writer: std::sync::Arc<std::sync::Mutex<Connection>>,
}

impl Slot for SqliteSlot {
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        self.reader
            .query_row(
                "SELECT tenant, id, name, \"group\", payload FROM users \
                 WHERE tenant = ?1 AND id = ?2",
                params![document::TENANT, document::seeded_id(seeded_index)],
                |_| Ok(()),
            )
            .map_err(|error| format!("seeded read: {error}").into())
    }

    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
        let writer = self
            .writer
            .lock()
            .map_err(|_| "writer connection poisoned by an earlier failure")?;
        let affected = writer.execute(
            "INSERT INTO users(tenant, id, name, \"group\", payload) \
             VALUES (?1, ?2, ?3, ?4, ?5) \
             ON CONFLICT (tenant, id) DO UPDATE SET \
                 name = excluded.name, \
                 \"group\" = excluded.\"group\", \
                 payload = excluded.payload",
            params![
                document::TENANT,
                document::measured_id(doc_index),
                document::MEASURED_NAME,
                document::MEASURED_GROUP,
                document::payload(),
            ],
        )?;
        assert_eq!(affected, 1, "a measured write must affect exactly one row");
        Ok(())
    }

    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        self.reader
            .query_row(
                "SELECT id, name FROM users WHERE tenant = ?1 AND name = ?2 \
                 ORDER BY name LIMIT 1",
                params![document::TENANT, document::seeded_name(seeded_index)],
                |_| Ok(()),
            )
            .map_err(|error| format!("indexed equality query: {error}").into())
    }
}
