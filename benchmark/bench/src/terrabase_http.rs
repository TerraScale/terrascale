//! The direct public-boundary TerraBase point-operation driver.
//!
//! Drives the served member's `/api/v1` HTTP surface exactly as a customer
//! workload would: one `ureq` agent per concurrency slot, point reads by
//! document id, acknowledged create/replace writes by id, and indexed
//! equality queries through the SQL endpoint. This is the boundary class
//! the retirement of the legacy served adapter named: no engine internals,
//! no in-process shortcuts, every operation a real HTTP request.
//!
//! Endpoint and credential come from the operator environment (see
//! `bench/scripts/local-member.sh env` for a local fenced subject):
//!
//! ```text
//! TERRABASE_BENCH_TERRABASE_API      base URL, e.g. http://127.0.0.1:7211
//! TERRABASE_BENCH_TERRABASE_AUTH     full Authorization header value
//! TERRABASE_BENCH_TERRABASE_DATABASE database name (default: local)
//! ```

use std::error::Error;

use ureq::Agent;

use crate::config::LaneConfig;
use crate::lane::{Backend, Slot, document};

const API_VARIABLE: &str = "TERRABASE_BENCH_TERRABASE_API";
const AUTH_VARIABLE: &str = "TERRABASE_BENCH_TERRABASE_AUTH";
const DATABASE_VARIABLE: &str = "TERRABASE_BENCH_TERRABASE_DATABASE";
const COLLECTION: &str = "users";

/// The administration handle: one agent for setup, seeding, and identity.
pub struct TerraBaseHttpBackend {
    agent: Agent,
    api: String,
    auth: String,
    database: String,
}

fn configuration() -> Result<(String, String, String), Box<dyn Error + Send + Sync>> {
    let api = std::env::var(API_VARIABLE)
        .map_err(|_| format!("{API_VARIABLE} is not set (the served member's base URL)"))?;
    let api = api.trim_end_matches('/').to_owned();
    let auth = std::env::var(AUTH_VARIABLE)
        .map_err(|_| format!("{AUTH_VARIABLE} is not set (the Authorization header value)"))?;
    let database =
        std::env::var(DATABASE_VARIABLE).unwrap_or_else(|_| "local".to_owned());
    Ok((api, auth, database))
}

impl TerraBaseHttpBackend {
    fn documents_url(&self, id: &str) -> String {
        format!(
            "{}/api/v1/databases/{}/collections/{}/documents/{}",
            self.api, self.database, COLLECTION, id
        )
    }

    fn batch_url(&self) -> String {
        format!(
            "{}/api/v1/databases/{}/collections/{}/documents/batch",
            self.api, self.database, COLLECTION
        )
    }

    fn sql_url(&self) -> String {
        format!("{}/api/v1/databases/{}/sql", self.api, self.database)
    }
}

impl Backend for TerraBaseHttpBackend {
    fn connect() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let (api, auth, database) = configuration()?;
        Ok(Self { agent: ureq::AgentBuilder::new().build(), api, auth, database })
    }

    fn target_name(&self) -> &'static str {
        "terrabase"
    }

    fn observed_identity(&mut self) -> Result<String, Box<dyn Error + Send + Sync>> {
        let response = self
            .agent
            .get(&format!("{}/api/v1/health", self.api))
            .set("Authorization", &self.auth)
            .timeout(std::time::Duration::from_secs(10))
            .call()
            .map_err(|error| format!("health probe failed: {error}"))?;
        Ok(response.into_string()?)
    }

    fn endpoint(&self) -> String {
        format!("{}/api/v1 (database {})", self.api, self.database)
    }

    fn client_transport(&self) -> &'static str {
        "ureq 2 HTTP/1.1, one connection-pooled agent per concurrency slot, \
         Bearer API key, acknowledged JSON document operations"
    }

    fn setup(&mut self, _config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        // The served surface has no truncate; a fresh run deletes the lane's
        // deterministic id ranges through the batch endpoint before warm-up.
        Ok(())
    }

    fn seed(&mut self, config: &LaneConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
        const BATCH: usize = 1000; // the batch endpoint's documented maximum
        let payload = document::payload();
        for first in (0..config.seed_documents).step_by(BATCH) {
            let count = BATCH.min(config.seed_documents - first);
            let operations = (0..count)
                .map(|offset| {
                    let index = first + offset;
                    serde_json::json!({
                        "op": "put",
                        "id": document::seeded_id(index),
                        "value": {
                            "name": document::seeded_name(index),
                            "group": document::seeded_group(index),
                            "payload": payload,
                        }
                    })
                })
                .collect::<Vec<_>>();
            let response = self
                .agent
                .post(&self.batch_url())
                .set("Authorization", &self.auth)
                .set("Content-Type", "application/json")
                .send_string(&serde_json::json!({ "operations": operations }).to_string())
                .map_err(|error| format!("seed batch {first} failed: {error}"))?;
            if response.status() != 200 {
                return Err(format!("seed batch {first} returned {}", response.status()).into());
            }
        }
        Ok(())
    }

    fn open_slot(&self) -> Result<Box<dyn Slot + Send>, Box<dyn Error + Send + Sync>> {
        Ok(Box::new(TerraBaseHttpSlot {
            agent: ureq::AgentBuilder::new().build(),
            api: self.api.clone(),
            auth: self.auth.clone(),
            database: self.database.clone(),
        }))
    }

    fn limits(&self) -> Vec<String> {
        vec![
            "the served member is the operator-supplied subject; its envelope and artifact \
             identity ride the run's comparability digests"
                .to_owned(),
            "reads and writes are acknowledged JSON document operations over HTTP/1.1 with \
             connection reuse; TLS is absent on the loopback dev subject"
                .to_owned(),
        ]
    }
}

struct TerraBaseHttpSlot {
    agent: Agent,
    api: String,
    auth: String,
    database: String,
}

impl TerraBaseHttpSlot {
    fn documents_url(&self, id: &str) -> String {
        format!(
            "{}/api/v1/databases/{}/collections/{}/documents/{}",
            self.api, self.database, COLLECTION, id
        )
    }

    fn sql_url(&self) -> String {
        format!("{}/api/v1/databases/{}/sql", self.api, self.database)
    }
}

impl Slot for TerraBaseHttpSlot {
    fn read(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let response = self
            .agent
            .get(&self.documents_url(&document::seeded_id(seeded_index)))
            .set("Authorization", &self.auth)
            .timeout(std::time::Duration::from_secs(10))
            .call()
            .map_err(|error| format!("read failed: {error}"))?;
        if response.status() != 200 {
            return Err(format!("read returned {}", response.status()).into());
        }
        let body = response.into_string()?;
        // The document body carries the value, not the id: the seeded name
        // proves the requested document came back.
        if !body.contains(&document::seeded_name(seeded_index)) {
            return Err("a seeded read must return the requested document".into());
        }
        Ok(())
    }

    fn write(&mut self, doc_index: u64) -> Result<(), Box<dyn Error + Send + Sync>> {
        let response = self
            .agent
            .put(&self.documents_url(&document::measured_id(doc_index)))
            .set("Authorization", &self.auth)
            .set("Content-Type", "application/json")
            .timeout(std::time::Duration::from_secs(10))
            .send_string(
                &serde_json::json!({
                    "name": document::MEASURED_NAME,
                    "group": document::MEASURED_GROUP,
                    "payload": document::payload(),
                })
                .to_string(),
            )
            .map_err(|error| format!("write failed: {error}"))?;
        if response.status() != 200 {
            return Err(format!("write returned {}", response.status()).into());
        }
        Ok(())
    }

    fn query(&mut self, seeded_index: usize) -> Result<(), Box<dyn Error + Send + Sync>> {
        let response = self
            .agent
            .post(&self.sql_url())
            .set("Authorization", &self.auth)
            .set("Content-Type", "application/json")
            .timeout(std::time::Duration::from_secs(10))
            .send_string(
                &serde_json::json!({
                    "sql": format!(
                        "SELECT _id, name FROM {COLLECTION} WHERE name = :name LIMIT 1"
                    ),
                    "parameters": { "name": document::seeded_name(seeded_index) },
                })
                .to_string(),
            )
            .map_err(|error| format!("query failed: {error}"))?;
        if response.status() != 200 {
            return Err(format!("query returned {}", response.status()).into());
        }
        let body = response.into_string()?;
        if !body.contains(&document::seeded_id(seeded_index)) {
            return Err("an indexed query must return the seeded row".into());
        }
        Ok(())
    }
}
