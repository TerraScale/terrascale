//! A database handle and its management metadata.

use futures_util::StreamExt;
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Serialize};

use super::client::{Client, Req};
use super::errors::{Error, Result};
use super::ids::check_name;

/// A handle to one database by current name or immutable ULID. Performs no I/O.
#[derive(Debug, Clone)]
pub struct Database {
    client: Client,
    name: String,
}

impl Database {
    pub(crate) fn new(client: Client, name: String) -> Self {
        Self { client, name }
    }

    /// The selector used to construct this handle.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// The client this handle uses.
    #[must_use]
    pub fn client(&self) -> &Client {
        &self.client
    }

    /// Streams the database as one SQL dump (BEGIN … COMMIT) from one read
    /// snapshot; the same SQL dialect runs it back into a fresh database.
    /// Requires `sql:read`; the database's home region serves the export.
    pub async fn export(&self) -> Result<impl futures_util::Stream<Item = Result<bytes::Bytes>>> {
        check_name("database", &self.name)?;
        let response = self
            .client
            .send_stream(Req::new(
                Method::GET,
                vec!["v1", "databases", &self.name, "export"],
            ))
            .await?;
        Ok(response
            .bytes_stream()
            .map(|chunk| chunk.map_err(|e| Error::Transport(super::client::describe(&e)))))
    }

    /// Creates this database through the project management API.
    /// Requires `databases:write`; a conflict is returned for an existing name.
    pub async fn create(&self) -> Result<ProjectDatabaseInfo> {
        check_name("database", &self.name)?;
        self.client
            .send_project(
                Req::new(Method::POST, vec!["v1", "databases"])
                    .json(&serde_json::json!({"name": self.name}))?,
            )
            .await?
            .decode_status(&[StatusCode::CREATED])
    }
}

/// Database metadata from the project management API. A database lives in one region.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProjectDatabaseInfo {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub project_id: String,
    pub name: String,
    pub branched_from: Option<String>,
    /// Contested name lost to another database during cross-region convergence.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous_name: Option<String>,
    /// Immutable ULID of the database that kept the contested name.
    #[serde(
        default,
        deserialize_with = "super::ids::optional_entity_id",
        skip_serializing_if = "Option::is_none"
    )]
    pub conflict_of: Option<String>,
    pub created_at: String,
    #[serde(default)]
    pub expires_at: Option<String>,
    pub region: String,
    pub home_region: String,
    #[serde(default)]
    pub replica_regions: Vec<String>,
    #[serde(default)]
    pub all_regions: bool,
}
