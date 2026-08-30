//! The pinned PostgreSQL reference driver.
//!
//! The exact server and client configuration is decided by the SE-15 pin
//! (`docs/benchmarks/se15-postgresql-reference-pin.md`, summarized in
//! `src/benchmarks/reference.rs`): PostgreSQL 18 at the artifact's shipped
//! defaults plus `synchronous_commit = on`, `fsync = on`,
//! `full_page_writes = on`; a fresh empty data directory per run on the
//! same storage class as the engine location; one table
//! `users(tenant, id, name, "group", payload)` with a primary key on
//! `(tenant, id)` and a btree index on `(tenant, name)` created before
//! warm-up; one prepared-statement connection per concurrency slot;
//! autocommit per statement.
//!
//! The server is started by `bench/scripts/pg-start.sh`; this driver only
//! connects, so the observed identity it records is the server's own
//! `SELECT version()` output, never a guess.

use std::error::Error;

use postgres::{Client, NoTls, Statement};

use crate::config::LaneConfig;
use crate::lane::{Backend, Slot, document};

/// The environment variable that names the pinned server's endpoint at a
/// lane run — the same variable `src/benchmarks/reference.rs` records. The
/// value the pinned server script starts is
/// `postgres://bench@127.0.0.1:55432/postgres`.
const ENDPOINT_VARIABLE: &str = "TERRABASE_PG_REFERENCE_ENDPOINT";

/// The endpoint this run connects to, from the environment.
fn endpoint() -> Result<String, Box<dyn Error + Send + Sync>> {
    std::env::var(ENDPOINT_VARIABLE).map_err(|_| format!("{ENDPOINT_VARIABLE} is not set").into())
}

/// The administration connection: schema, seeding, and identity capture.
pub struct PostgreSqlBackend {
    admin: Client,
    endpoint: String,
}

impl Backend for PostgreSqlBackend {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let endpoint = endpoint()?;
        let admin = Client::connect(&endpoint, NoTls)?;
        Ok(Self { admin, endpoint })
    }

    fn target_name(&self) -> &'static str {
        "postgresql"
    }

    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
        let row = self.admin.query_one("SELECT version()", &[])?;
        Ok(row.get::<_, String>(0))
    }

    fn endpoint(&self) -> String {
        self.endpoint.clone()
    }

    fn client_transport(&self) -> &'static str {
        "rust postgres crate 0.19, one synchronous TCP connection per concurrency slot, \
         prepared statements, autocommit per statement"
    }

    fn setup(&mut self, _config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // A fresh data directory per run is the server script's job; here a
        // fresh schema in that directory is enough, and the pin's shape is
        // created exactly as documented.
        self.admin.batch_execute(
            "DROP TABLE IF EXISTS users;\n\
             CREATE TABLE users(\n\
                 tenant text NOT NULL,\n\
                 id text NOT NULL,\n\
                 name text NOT NULL,\n\
                 \"group\" int NOT NULL,\n\
                 payload text NOT NULL,\n\
                 PRIMARY KEY (tenant, id));\n\
             CREATE INDEX users_tenant_name_idx ON users (tenant, name);",
        )?;
        Ok(())
    }

    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        let insert = self.admin.prepare(
            "INSERT INTO users(tenant, id, name, \"group\", payload) \
             VALUES ($1, $2, $3, $4, $5)",
        )?;
        let payload = document::payload();
        for index in 0..config.seed_documents {
            self.admin.execute(
                &insert,
                &[
                    &document::TENANT.to_owned(),
                    &document::seeded_id(index),
                    &document::seeded_name(index),
                    &document::seeded_group(index),
                    &payload,
                ],
            )?;
        }
        Ok(())
    }

    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
        Ok(Box::new(PostgreSqlSlot::connect()?))
    }

    fn limits(&self) -> Vec<String> {
        vec![
            "server-side configuration is the SE-15 pin's, captured verbatim on the receipt"
                .to_owned(),
            "planner and memory settings are the pinned artifact's shipped defaults; \
             no ANALYZE beyond what warm-up naturally produces"
                .to_owned(),
        ]
    }
}

/// One concurrency slot: one connection with three prepared statements.
struct PostgreSqlSlot {
    connection: Client,
    read: Statement,
    write: Statement,
    query: Statement,
}

impl PostgreSqlSlot {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let mut connection = Client::connect(&endpoint()?, NoTls)?;
        let read = connection.prepare(
            "SELECT tenant, id, name, \"group\", payload FROM users WHERE tenant = $1 AND id = $2",
        )?;
        let write = connection.prepare(
            "INSERT INTO users(tenant, id, name, \"group\", payload) \
             VALUES ($1, $2, $3, $4, $5) \
             ON CONFLICT (tenant, id) DO UPDATE SET \
                 name = EXCLUDED.name, \
                 \"group\" = EXCLUDED.\"group\", \
                 payload = EXCLUDED.payload",
        )?;
        let query = connection.prepare(
            "SELECT id, name FROM users WHERE tenant = $1 AND name = $2 \
             ORDER BY name LIMIT 1",
        )?;
        Ok(Self { connection, read, write, query })
    }
}

impl Slot for PostgreSqlSlot {
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let tenant = document::TENANT.to_owned();
        let id = document::seeded_id(seeded_index);
        let rows = self.connection.query(&self.read, &[&tenant, &id])?;
        assert_eq!(rows.len(), 1, "a seeded read must return exactly one row");
        Ok(())
    }

    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
        let tenant = document::TENANT.to_owned();
        let id = document::measured_id(doc_index);
        let name = document::MEASURED_NAME.to_owned();
        let group = document::MEASURED_GROUP;
        let payload = document::payload();
        let affected =
            self.connection.execute(&self.write, &[&tenant, &id, &name, &group, &payload])?;
        assert_eq!(affected, 1, "a measured write must affect exactly one row");
        Ok(())
    }

    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let tenant = document::TENANT.to_owned();
        let name = document::seeded_name(seeded_index);
        let rows = self.connection.query(&self.query, &[&tenant, &name])?;
        assert_eq!(rows.len(), 1, "an indexed equality query must return exactly one row");
        Ok(())
    }
}
