//! The pinned RavenDB reference driver.
//!
//! RavenDB is the document-database comparand the product objective names
//! beside PostgreSQL. The pinned identity is the official linux-x64
//! server from the stable channel (`docker.io/ravendb/ravendb:latest`),
//! started by `bench/scripts/raven-start.sh` on loopback only with a fresh
//! data directory per run; the exact build the server reports is captured
//! verbatim from its own `/build/version` endpoint at the run.
//!
//! Configuration is the artifact's shipped defaults: unsecured loopback
//! HTTP, one keep-alive connection per concurrency slot, RavenDB's own
//! auto-indexing (the reference's shipped behavior, absorbed during
//! warm-up exactly as the TerraBase side's auto-index lifecycle is).
//! Writes are single-document PUTs acknowledged by the server's own
//! durability defaults; no tuning was applied to either side.

use std::error::Error;
use std::time::Duration;

use ureq::Agent;

use crate::config::LaneConfig;
use crate::lane::{document, Backend, Slot};

/// The environment variable that names the pinned server's endpoint at a
/// lane run, mirroring the PostgreSQL pin's variable. The value the
/// pinned server script starts is `http://127.0.0.1:8091`.
const ENDPOINT_VARIABLE: &str = "TERRABASE_RAVEN_REFERENCE_ENDPOINT";

/// The endpoint this run connects to, from the environment.
fn endpoint() -> Result<String, Box<dyn Error + Send + Sync>> {
    std::env::var(ENDPOINT_VARIABLE).map_err(|_| format!("{ENDPOINT_VARIABLE} is not set").into())
}

/// The database name the lane runs against; one fresh database per run.
const DATABASE: &str = "terrabase-bench";

/// The administration handle: database lifecycle and identity capture.
pub struct RavenDbBackend {
    endpoint: String,
    database: String,
}

impl Backend for RavenDbBackend {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        Ok(Self { endpoint: endpoint()?, database: DATABASE.to_owned() })
    }

    fn target_name(&self) -> &'static str {
        "ravendb"
    }

    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
        let body = ureq::get(&format!("{}/build/version", self.endpoint))
            .timeout(Duration::from_secs(10))
            .call()?
            .into_string()?;
        let value: serde_json::Value = serde_json::from_str(&body)?;
        let full = value["FullVersion"].as_str().unwrap_or("unknown");
        let build = value["BuildVersion"].as_u64().unwrap_or(0);
        Ok(format!("RavenDB {full}, build {build} (server-reported /build/version)"))
    }

    fn endpoint(&self) -> String {
        format!("{}/databases/{}", self.endpoint, self.database)
    }

    fn client_transport(&self) -> &'static str {
        "rust ureq 2 HTTP/1.1 keep-alive, one agent connection per concurrency slot"
    }

    fn setup(&mut self, _config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // Fresh database per run: this run's database carries a per-run
        // suffix, so it is always created empty and no deletion race with
        // the server's raft bookkeeping can 409. The server script's
        // container (and with it the data directory) is discarded per lane
        // run, so per-run databases never accumulate across runs.
        let suffix = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|since| since.as_secs())
            .unwrap_or(0);
        self.database = format!("{DATABASE}-{suffix}");
        let create = ureq::put(&format!("{}/admin/databases", self.endpoint))
            .timeout(Duration::from_secs(60))
            .set("Content-Type", "application/json")
            .send_string(&format!(
                "{{\"DatabaseName\":\"{}\",\"Settings\":{{}}}}",
                self.database
            ))?;
        if create.status() != 201 && create.status() != 200 {
            return Err(format!("database creation returned {}", create.status()).into());
        }
        Ok(())
    }

    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        let agent = fresh_agent();
        let payload = document::payload();
        for index in 0..config.seed_documents {
            let id = format!("users/{}", document::seeded_id(index));
            let body = serde_json::json!({
                "name": document::seeded_name(index),
                "group": document::seeded_group(index),
                "payload": payload,
            })
            .to_string();
            let response = agent
                .put(&format!("{}/databases/{}/docs", self.endpoint, self.database))
                .query("id", &id)
                .set("Content-Type", "application/json")
                .send_string(&body)?;
            if response.status() != 201 {
                return Err(format!("seed PUT returned {}", response.status()).into());
            }
        }
        Ok(())
    }

    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
        Ok(Box::new(RavenDbSlot {
            endpoint: self.endpoint.clone(),
            database: self.database.clone(),
            agent: fresh_agent(),
        }))
    }

    fn limits(&self) -> Vec<String> {
        vec![
            "the reference's shipped defaults: auto-indexing on (the users-by-name auto \
             index is created during warm-up), server-acknowledged PUT durability, \
             no tuning applied to either side"
                .to_owned(),
            "unsecured loopback HTTP inside a rootless podman host-network container; \
             the engine side is an in-process library boundary, so the two sides differ \
             in boundary, stated here rather than hidden"
                .to_owned(),
        ]
    }
}

/// One keep-alive HTTP connection for one concurrency slot.
struct RavenDbSlot {
    endpoint: String,
    database: String,
    agent: Agent,
}

/// Builds a one-connection agent: `max_idle_connections_per_host(1)` pins
/// each slot to exactly one keep-alive connection, the HTTP analogue of
/// the PostgreSQL side's one connection per slot.
fn fresh_agent() -> Agent {
    ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_secs(30))
        .timeout_read(Duration::from_secs(60))
        .max_idle_connections(1)
        .max_idle_connections_per_host(1)
        .build()
}

impl Slot for RavenDbSlot {
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let id = format!("users/{}", document::seeded_id(seeded_index));
        let body = self
            .agent
            .get(&format!("{}/databases/{}/docs", self.endpoint, self.database))
            .query("id", &id)
            .call()?
            .into_string()?;
        if !body.contains("\"Results\":[") || body.contains("\"Results\":[]") {
            return Err(format!("read of {id} returned no document: {body}").into());
        }
        Ok(())
    }

    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
        let id = format!("users/{}", document::measured_id(doc_index));
        let body = serde_json::json!({
            "name": document::MEASURED_NAME,
            "group": document::MEASURED_GROUP,
            "payload": document::payload(),
        })
        .to_string();
        let response = self
            .agent
            .put(&format!("{}/databases/{}/docs", self.endpoint, self.database))
            .query("id", &id)
            .set("Content-Type", "application/json")
            .send_string(&body)?;
        if response.status() != 201 {
            return Err(format!("measured PUT returned {}", response.status()).into());
        }
        Ok(())
    }

    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let name = document::seeded_name(seeded_index);
        let rql = format!("from users where name = '{name}'");
        let body = self
            .agent
            .get(&format!("{}/databases/{}/queries", self.endpoint, self.database))
            .query("query", &rql)
            .query("pageSize", "1")
            .call()?
            .into_string()?;
        let value: serde_json::Value = serde_json::from_str(&body)?;
        let results = value["Results"].as_array();
        if !results.is_some_and(|rows| rows.len() == 1) {
            return Err(format!("query for {name} returned {body}").into());
        }
        Ok(())
    }
}
