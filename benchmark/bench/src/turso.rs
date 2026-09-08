//! The embedded Turso reference driver: the Rust rewrite of SQLite, raw.
//!
//! Turso's native Rust API is async, so the SQLite-pin analogue is: WAL
//! journal with `synchronous = FULL` and a five-second busy timeout on every
//! connection; one table `users(tenant, id, name, "group", payload)` with a
//! primary key on `(tenant, id)` and an index on `(tenant, name)` created
//! before warm-up; one connection per concurrency slot taking a statement
//! from the connection's cache per operation (a statement held across the
//! lane pins its prepare-time snapshot, which sibling writers invalidate —
//! the stale-snapshot failure the first smoke run caught); autocommit per
//! statement. Each slot drives its connection through its own current-thread
//! tokio runtime, `block_on` per operation — the standard shape for a
//! synchronous caller of an async engine, and the closest embedded match to
//! how every other reference in this lane is driven. There is no server: the
//! measured member is this driver's own process, stated in `limits`.

use std::error::Error;
use std::path::PathBuf;
use std::time::Duration;

use crate::config::LaneConfig;
use crate::lane::{Backend, Slot, document};

/// The environment variable that names the reference file. The default
/// lives under `bench/target/` so the database sits on the same real
/// filesystem as every other reference run, never on a tmpfs.
const PATH_VARIABLE: &str = "TERRABASE_TURSO_REFERENCE_PATH";
const DEFAULT_PATH: &str = "bench/target/turso-reference.db";

/// The file this run opens, from the environment or the default.
fn reference_path() -> PathBuf {
    std::env::var(PATH_VARIABLE).map_or_else(|_| PathBuf::from(DEFAULT_PATH), PathBuf::from)
}

/// How long one write may keep retrying turso's MVCC conflicts before the
/// lane classifies the run as failed; the pin's five-second busy timeout
/// bounds each individual lock wait inside that budget.
const WRITE_RETRY_DEADLINE: Duration = Duration::from_secs(30);

/// Opens one connection with the pin's pragmas and busy timeout.
async fn pin(connection: &turso::Connection) -> Result<(), turso::Error> {
    connection.pragma_update("journal_mode", "wal").await?;
    connection.pragma_update("synchronous", "FULL").await?;
    connection.busy_timeout(Duration::from_millis(5_000))?;
    Ok(())
}

/// The administration side: the database handle, one connection for schema,
/// seeding, and identity capture, and the runtime that drives them.
pub struct TursoBackend {
    database: turso::Database,
    admin: turso::Connection,
    runtime: tokio::runtime::Runtime,
    path: PathBuf,
}

impl Backend for TursoBackend {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let path = reference_path();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        // A fresh store per run, the pin's discipline: any prior run's data,
        // including its WAL siblings, is discarded.
        for suffix in ["", "-wal", "-shm"] {
            let _ = std::fs::remove_file(format!("{}{suffix}", path.display()));
        }
        let path_text = path.to_str().ok_or("the turso reference path is not valid UTF-8")?;
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        let database = runtime.block_on(turso::Builder::new_local(path_text).build())?;
        let admin = database.connect()?;
        runtime.block_on(pin(&admin))?;
        Ok(Self { database, admin, runtime, path })
    }

    fn target_name(&self) -> &'static str {
        "turso"
    }

    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
        let sqlite_version = self.runtime.block_on(async {
            let mut rows = self.admin.query("SELECT sqlite_version()", ()).await?;
            let row = rows
                .next()
                .await?
                .ok_or(turso::Error::QueryReturnedNoRows)?;
            Ok::<_, turso::Error>(row.get::<String>(0)?)
        })?;
        Ok(format!(
            "Turso (Rust SQLite rewrite), SQL compatibility {sqlite_version}; journal_mode=wal; \
             synchronous=FULL; busy_timeout=5000ms"
        ))
    }

    fn endpoint(&self) -> String {
        format!("file://{}", self.path.display())
    }

    fn client_transport(&self) -> &'static str {
        "turso crate, one embedded async connection per concurrency slot driven through a \
         current-thread runtime, connection-cached statement per operation, autocommit per \
         statement"
    }

    fn setup(&mut self, _config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // The same shape as the PostgreSQL and SQLite pins' table and index.
        self.runtime
            .block_on(self.admin.execute_batch(
                "DROP TABLE IF EXISTS users;\n\
                 CREATE TABLE users(\n\
                     tenant TEXT NOT NULL,\n\
                     id TEXT NOT NULL,\n\
                     name TEXT NOT NULL,\n\
                     \"group\" INTEGER NOT NULL,\n\
                     payload TEXT NOT NULL,\n\
                     PRIMARY KEY (tenant, id));\n\
                 CREATE INDEX users_tenant_name_idx ON users (tenant, name);",
            ))?;
        Ok(())
    }

    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        let payload = document::payload();
        self.runtime.block_on(async {
            let mut insert = self
                .admin
                .prepare(
                    "INSERT INTO users(tenant, id, name, \"group\", payload) \
                     VALUES (?1, ?2, ?3, ?4, ?5)",
                )
                .await?;
            for index in 0..config.seed_documents {
                insert
                    .execute(turso::params![
                        document::TENANT.to_owned(),
                        document::seeded_id(index),
                        document::seeded_name(index),
                        document::seeded_group(index),
                        payload.clone(),
                    ])
                    .await?;
            }
            Ok::<_, turso::Error>(())
        })?;
        Ok(())
    }

    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        let connection = self.database.connect()?;
        runtime.block_on(pin(&connection))?;
        Ok(Box::new(TursoSlot { runtime, connection }))
    }

    fn limits(&self) -> Vec<String> {
        vec![
            "embedded reference: the measured member is this driver's own process, so the load \
             generator and the database share one resource envelope"
                .to_owned(),
            "the engine's native Rust API is async: each slot drives its connection through its \
             own current-thread runtime with block_on per operation, so per-operation latencies \
             include that boundary"
                .to_owned(),
            "sixteen per-slot writers contend through turso's MVCC: stale-snapshot and busy \
             conflicts are retried per turso's documented pattern, bounded by a 30-second \
             per-operation deadline, and every retry's wait is part of the measured latency"
                .to_owned(),
        ]
    }
}

/// One concurrency slot: its own connection and the current-thread runtime
/// that drives it. Statements come from the connection's cache per
/// operation: a statement reused across the lane pinning its prepare-time
/// snapshot is exactly the stale-snapshot failure the smoke run caught, so
/// the slot deliberately keeps no long-lived statements.
struct TursoSlot {
    runtime: tokio::runtime::Runtime,
    connection: turso::Connection,
}

impl TursoSlot {
    /// Drives one future on a slot's runtime. An associated function so the
    /// statement borrow and the runtime borrow stay disjoint.
    fn drive<T>(
        runtime: &tokio::runtime::Runtime,
        future: impl std::future::Future<Output = Result<T, turso::Error>>,
        context: &str,
    ) -> Result<T, Box<dyn Error + Send + Sync>> {
        runtime
            .block_on(future)
            .map_err(|error| format!("{context}: {error}").into())
    }
}

/// Turso's documented concurrent-write pattern: lock and MVCC conflicts are
/// surfaced to the caller as retryable errors ("give up and let caller
/// retry from scratch" is the engine's own wording for the stale-snapshot
/// path), so a writer retries the whole statement.
fn is_retryable(error: &turso::Error) -> bool {
    matches!(error, turso::Error::Busy(_) | turso::Error::BusySnapshot(_))
        || matches!(error, turso::Error::Error(message) if message.contains("conflict") || message.contains("stale"))
}

impl Slot for TursoSlot {
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let tenant = document::TENANT.to_owned();
        let id = document::seeded_id(seeded_index);
        Self::drive(
            &self.runtime,
            async {
                let mut statement = self
                    .connection
                    .prepare_cached(
                        "SELECT tenant, id, name, \"group\", payload FROM users \
                         WHERE tenant = ?1 AND id = ?2",
                    )
                    .await?;
                let mut rows = statement.query(turso::params![tenant, id]).await?;
                match rows.next().await? {
                    Some(_) => Ok(()),
                    None => Err(turso::Error::QueryReturnedNoRows),
                }
            },
            "seeded read",
        )
    }

    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
        let tenant = document::TENANT.to_owned();
        let id = document::measured_id(doc_index);
        let name = document::MEASURED_NAME.to_owned();
        let payload = document::payload();
        let runtime = &self.runtime;
        let connection = &self.connection;
        let deadline = std::time::Instant::now() + WRITE_RETRY_DEADLINE;
        let mut attempts = 0_u64;
        loop {
            attempts += 1;
            // Turso's concurrent-write shape from its own integration tests:
            // a fresh statement per operation through `connection::execute`
            // (a reused prepared statement pins its prepare-time snapshot,
            // which any sibling writer instantly invalidates), autocommit,
            // retry on conflict.
            let result = runtime.block_on(async {
                connection
                    .execute(
                        "INSERT INTO users(tenant, id, name, \"group\", payload) \
                         VALUES (?1, ?2, ?3, ?4, ?5) \
                         ON CONFLICT (tenant, id) DO UPDATE SET \
                             name = excluded.name, \
                             \"group\" = excluded.\"group\", \
                             payload = excluded.payload",
                        turso::params![
                            tenant.clone(),
                            id.clone(),
                            name.clone(),
                            document::MEASURED_GROUP,
                            payload.clone()
                        ],
                    )
                    .await
            });
            match result {
                Ok(affected) => {
                    assert_eq!(affected, 1, "a measured write must affect exactly one row");
                    return Ok(());
                }
                Err(error)
                    if is_retryable(&error) && std::time::Instant::now() < deadline =>
                {
                    // The failed transaction must be rolled back before the
                    // retry can observe a fresh snapshot — turso's own
                    // concurrent-writes pattern, whose rollback outcome is
                    // discarded the same way.
                    let _ = runtime
                        .block_on(async { connection.execute("ROLLBACK", ()).await });
                    std::thread::sleep(Duration::from_millis(1));
                }
                Err(error) => {
                    return Err(format!("measured write (attempt {attempts}): {error}").into());
                }
            }
        }
    }

    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let tenant = document::TENANT.to_owned();
        let name = document::seeded_name(seeded_index);
        Self::drive(
            &self.runtime,
            async {
                let mut statement = self
                    .connection
                    .prepare_cached(
                        "SELECT id, name FROM users WHERE tenant = ?1 AND name = ?2 \
                         ORDER BY name LIMIT 1",
                    )
                    .await?;
                let mut rows = statement.query(turso::params![tenant, name]).await?;
                match rows.next().await? {
                    Some(_) => Ok(()),
                    None => Err(turso::Error::QueryReturnedNoRows),
                }
            },
            "indexed equality query",
        )
    }
}
