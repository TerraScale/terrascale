//! The pinned PostgreSQL full-stack reference driver.
//!
//! The engine-vs-engine PostgreSQL lane (`pg.rs`) measures the database
//! alone over raw prepared-statement TCP. This lane measures the equivalent
//! *served* stack a PostgreSQL deployment actually ships: PostgreSQL 18
//! holding documents as jsonb, behind pgbouncer in transaction-pooling
//! mode, behind a minimal Go HTTP/JSON document service
//! (`terrabase-go/bench/pgrest`), started by
//! `bench/scripts/pg-fullstack-start.sh`. The driver speaks
//! HTTP/1.1 keep-alive to that service exactly the way `raven.rs` speaks to
//! RavenDB: one ureq agent, one keep-alive connection, per concurrency
//! slot. Every operation pays real JSON encode/decode on both sides of the
//! wire, which is the point of the full-stack boundary.
//!
//! The document contract is `lane.rs`'s, unchanged: tenant
//! `benchmark-tenant`, ids `seed-{i}`/`measured-{i}`, names
//! `user-{i}`/`measured`, groups `i % 100`/42, 256-byte payload.

use std::error::Error;
use std::time::Duration;

use ureq::Agent;

use crate::config::LaneConfig;
use crate::lane::{Backend, Slot, document};

/// The environment variable that names the pinned stack's HTTP endpoint at
/// a lane run, mirroring the other pins' variables. The value the pinned
/// stack script starts is `http://127.0.0.1:55433`.
const ENDPOINT_VARIABLE: &str = "TERRABASE_PG_FULLSTACK_ENDPOINT";

/// The endpoint the pinned stack script starts; the environment variable
/// overrides it, and unlike the engine-side pins a default exists because
/// the stack's own script is the only supported way to start it.
const DEFAULT_ENDPOINT: &str = "http://127.0.0.1:55433";

/// The endpoint this run connects to, from the environment or the default.
fn endpoint() -> Result<String, Box<dyn Error + Send + Sync>> {
    Ok(std::env::var(ENDPOINT_VARIABLE).unwrap_or_else(|_| DEFAULT_ENDPOINT.to_owned()))
}

/// The administration handle: schema reset, seeding, and identity capture.
pub struct PgFullstackBackend {
    endpoint: String,
}

impl Backend for PgFullstackBackend {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        Ok(Self { endpoint: endpoint()? })
    }

    fn target_name(&self) -> &'static str {
        "pg-fullstack"
    }

    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
        // The service reports the PostgreSQL `SELECT version()` string it
        // observed through pgbouncer at startup, so the identity is the
        // server's own, not a guess, with the stack named around it.
        let body = ureq::get(&format!("{}/healthz", self.endpoint))
            .timeout(Duration::from_secs(10))
            .call()?
            .into_string()?;
        let value: serde_json::Value = serde_json::from_str(&body)?;
        let postgres = value["postgres_version"].as_str().unwrap_or("unknown");
        Ok(format!(
            "{postgres}, behind pgbouncer (transaction pooling) behind the pgrest HTTP/JSON \
             service (service-reported /healthz)"
        ))
    }

    fn endpoint(&self) -> String {
        self.endpoint.clone()
    }

    fn client_transport(&self) -> &'static str {
        "postgres 18 jsonb documents behind pgbouncer transaction pooling behind a Go stdlib \
         HTTP/JSON service; rust ureq 2 HTTP/1.1 keep-alive, one agent connection per \
         concurrency slot"
    }

    fn setup(&mut self, _config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // A fresh data directory per run is the stack script's job; the
        // service's reset endpoint recreates the schema so this run starts
        // from an empty store exactly like the engine-side driver's setup.
        let response = ureq::post(&format!("{}/admin/reset", self.endpoint))
            .timeout(Duration::from_secs(30))
            .call()?;
        if response.status() != 200 {
            return Err(format!("schema reset returned {}", response.status()).into());
        }
        Ok(())
    }

    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // Seeding rides the same HTTP PUT path the measured writes ride:
        // the full stack, jsonb encoding included, is what this lane seeds
        // and measures.
        let agent = fresh_agent();
        let payload = document::payload();
        for index in 0..config.seed_documents {
            let body = serde_json::json!({
                "name": document::seeded_name(index),
                "group": document::seeded_group(index),
                "payload": payload,
            })
            .to_string();
            let response = agent
                .put(&format!(
                    "{}/documents/{}/{}",
                    self.endpoint,
                    document::TENANT,
                    document::seeded_id(index)
                ))
                .set("Content-Type", "application/json")
                .send_string(&body)?;
            if response.status() != 200 {
                return Err(format!("seed PUT returned {}", response.status()).into());
            }
        }
        Ok(())
    }

    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
        Ok(Box::new(PgFullstackSlot { endpoint: self.endpoint.clone(), agent: fresh_agent() }))
    }

    fn limits(&self) -> Vec<String> {
        vec![
            "the measured boundary deliberately includes pgbouncer (transaction pooling) and \
             the pgrest HTTP/JSON layer; JSON encode/decode on both sides of the wire is part \
             of every operation, not overhead to apologize for"
                .to_owned(),
            "server-side PostgreSQL configuration is the SE-15 pin's (synchronous_commit=on, \
             fsync=on, full_page_writes=on), captured verbatim on the receipt"
                .to_owned(),
            "planner and memory settings are the pinned artifact's shipped defaults; \
             no ANALYZE beyond what warm-up naturally produces"
                .to_owned(),
        ]
    }
}

/// One keep-alive HTTP connection for one concurrency slot, the HTTP
/// analogue of the engine-side lane's one database connection per slot.
struct PgFullstackSlot {
    endpoint: String,
    agent: Agent,
}

/// Builds a one-connection agent, mirroring `raven.rs`:
/// `max_idle_connections_per_host(1)` pins each slot to exactly one
/// keep-alive connection.
fn fresh_agent() -> Agent {
    ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_secs(30))
        .timeout_read(Duration::from_secs(60))
        .max_idle_connections(1)
        .max_idle_connections_per_host(1)
        .build()
}

impl Slot for PgFullstackSlot {
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let id = document::seeded_id(seeded_index);
        let body = self
            .agent
            .get(&format!("{}/documents/{}/{}", self.endpoint, document::TENANT, id))
            .call()?
            .into_string()?;
        let value: serde_json::Value = serde_json::from_str(&body)?;
        // A 404 surfaces as a transport error before this point, so a body
        // that is not the requested document means the lane's own contract
        // broke and the run must fail, not measure through it.
        if value["id"].as_str() != Some(id.as_str()) {
            return Err(format!("read of {id} returned a different document: {body}").into());
        }
        if value["payload"].as_str().is_none_or(str::is_empty) {
            return Err(format!("read of {id} returned no payload: {body}").into());
        }
        Ok(())
    }

    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
        let id = document::measured_id(doc_index);
        let body = serde_json::json!({
            "name": document::MEASURED_NAME,
            "group": document::MEASURED_GROUP,
            "payload": document::payload(),
        })
        .to_string();
        let response = self
            .agent
            .put(&format!("{}/documents/{}/{}", self.endpoint, document::TENANT, id))
            .set("Content-Type", "application/json")
            .send_string(&body)?;
        if response.status() != 200 {
            return Err(format!("measured PUT returned {}", response.status()).into());
        }
        let body = response.into_string()?;
        let value: serde_json::Value = serde_json::from_str(&body)?;
        if value["version"].as_i64().is_none_or(|version| version < 1) {
            return Err(format!("measured PUT of {id} was not acknowledged: {body}").into());
        }
        Ok(())
    }

    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let name = document::seeded_name(seeded_index);
        let body = self
            .agent
            .get(&format!("{}/query", self.endpoint))
            .query("tenant", document::TENANT)
            .query("name", &name)
            .query("limit", "1")
            .call()?
            .into_string()?;
        let value: serde_json::Value = serde_json::from_str(&body)?;
        if !value.as_array().is_some_and(|rows| rows.len() == 1) {
            return Err(format!("query for {name} returned {body}").into());
        }
        Ok(())
    }
}
