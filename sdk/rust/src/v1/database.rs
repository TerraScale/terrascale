//! A database handle and its management metadata.

use futures_util::StreamExt;
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Serialize};

use super::client::{Client, Req};
use super::errors::{Error, Result};
use super::ids::{check_database_selector, check_name};
use crate::consistency::{is_valid_bookmark, BookmarkTracker, ConsistencyMode};

/// A handle to one database by current name or immutable ULID. Performs no I/O.
///
/// Every region of the database is an equal active-active peer. Handles from
/// one [`Client`] for the same selector share a causal bookmark: each SQL
/// answer over HTTP or the WebSocket advances it and later SQL requests send
/// it as `TerraScale-Bookmark`, so any region reads this client's writes.
#[derive(Debug, Clone)]
pub struct Database {
    client: Client,
    name: String,
    bookmark: BookmarkTracker,
    consistency: Option<ConsistencyMode>,
}

impl Database {
    pub(crate) fn new(client: Client, name: String) -> Self {
        let bookmark = client.bookmark_tracker(&name);
        let consistency = client.consistency();
        Self {
            client,
            name,
            bookmark,
            consistency,
        }
    }

    /// A copy of this handle whose SQL defaults to `mode`.
    #[must_use]
    pub fn with_consistency(mut self, mode: ConsistencyMode) -> Self {
        self.consistency = Some(mode);
        self
    }

    /// The consistency mode this handle requests, when one is set; otherwise
    /// the database's configured default applies.
    #[must_use]
    pub fn consistency(&self) -> Option<ConsistencyMode> {
        self.consistency
    }

    /// The newest causal bookmark observed on this database.
    #[must_use]
    pub fn bookmark(&self) -> Option<String> {
        self.bookmark.get()
    }

    /// Merges a bookmark obtained elsewhere, for example from another client
    /// or a function invocation, so later SQL reads observe those writes.
    pub fn observe_bookmark(&self, bookmark: &str) -> Result<()> {
        check_bookmark(bookmark)?;
        self.bookmark.observe(bookmark);
        Ok(())
    }

    /// Forgets this database's causal bookmark.
    pub fn clear_bookmark(&self) {
        self.bookmark.clear();
    }

    pub(crate) fn bookmark_tracker(&self) -> &BookmarkTracker {
        &self.bookmark
    }

    /// Sets the database's default consistency for requests that send none
    /// (`PATCH P/databases/{database}`). Requires `databases:write`.
    pub async fn set_default_consistency(
        &self,
        mode: ConsistencyMode,
    ) -> Result<ProjectDatabaseInfo> {
        check_database_selector(&self.name)?;
        self.client
            .send_project(
                Req::new(Method::PATCH, vec!["v1", "databases", &self.name])
                    .json(&serde_json::json!({"defaultConsistency": mode}))?,
            )
            .await?
            .decode_status(&[StatusCode::OK])
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
    /// Requires `sql:read`; the region that orders the database's writes serves the export.
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

/// Validates a caller-supplied causal bookmark.
pub(crate) fn check_bookmark(bookmark: &str) -> Result<()> {
    if is_valid_bookmark(bookmark.trim()) {
        Ok(())
    } else {
        Err(Error::InvalidInput(
            "bookmark must be one TerraScale-Bookmark returned by TerraScale".into(),
        ))
    }
}

/// Lifecycle metadata: the answering region, the complete set of equal
/// active-active peer regions, the placement epoch and the default
/// consistency mode.
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
    #[serde(default)]
    pub regions: Vec<String>,
    #[serde(default)]
    pub all_regions: bool,
    pub placement_version: i64,
    /// The consistency mode SQL uses when a request names none.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub default_consistency: Option<ConsistencyMode>,
}
