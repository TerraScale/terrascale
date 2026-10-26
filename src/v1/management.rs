//! Canonical project management routes. Tooling bodies use the server's camelCase
//! JSON contract; deserialize into application structs or `serde_json::Value`.

use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};

use super::client::{Client, Req};
use super::documents::{Database, ProjectDatabaseInfo};
use super::errors::{Error, Result};
use super::ids::{check_database_selector, check_name, valid_entity_id};

/// Management pagination uses `after` / `next`, independently of data cursors.
#[derive(Debug, Clone, Deserialize)]
pub struct ManagementPage<T> {
    pub items: Vec<T>,
    pub next: Option<String>,
}

/// Placement supplied when creating or updating a database. Writable databases require
/// at least two distinct serving regions: the home region and at least one replica.
/// On create, omitting `replica_regions` uses all current serving regions;
/// setting only `home_region` does not request single-region placement.
/// An explicit empty replica list on create is rejected with HTTP 400.
/// `all_regions: Some(true)` also follows regions added later; the server rejects
/// it combined with `replica_regions` on create.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabasePlacement {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub home_region: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub replica_regions: Option<Vec<String>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub all_regions: Option<bool>,
}

impl Client {
    pub(crate) async fn management<T: DeserializeOwned>(
        &self,
        method: Method,
        segments: Vec<&str>,
        query: &[(&str, String)],
        body: Option<&impl Serialize>,
        status: StatusCode,
    ) -> Result<T> {
        let mut req = Req::new(method, segments);
        req.query = query.to_vec();
        if let Some(body) = body {
            req = req.json(body)?;
        }
        self.send_project(req).await?.decode_status(&[status])
    }

    /// Renames a database by immutable ULID (`PATCH P/databases/{database_id}`).
    /// Availability remains held pending the owning API; see docs/database-rename.md.
    /// Requires `databases:write`. Returns the existing database metadata; its
    /// ID, data, keys and placement stay unchanged. Invalid names return 400
    /// (or local validation errors); project-duplicate names return 409.
    /// Old name routes return 404. Update name-based connection strings and
    /// handles; ID-based connection strings remain valid. Reconcile uncertain
    /// failures before explicitly retrying this mutation.
    pub async fn rename_database(
        &self,
        database_id: &str,
        name: &str,
    ) -> Result<ProjectDatabaseInfo> {
        if !valid_entity_id(database_id) {
            return Err(Error::InvalidInput(
                "database ID must be a canonical uppercase ULID".into(),
            ));
        }
        check_name("database", name)?;
        self.management(
            Method::PATCH,
            vec!["v1", "databases", database_id],
            &[],
            Some(&serde_json::json!({"name": name})),
            StatusCode::OK,
        )
        .await
    }

    /// `GET P/databases`; `after` is an opaque management cursor.
    pub async fn list_databases(
        &self,
        query: &[(&str, String)],
    ) -> Result<ManagementPage<ProjectDatabaseInfo>> {
        self.management(
            Method::GET,
            vec!["v1", "databases"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
}

impl Database {
    /// `POST P/databases`, including explicit home and replica placement.
    pub async fn create_with_placement(
        &self,
        placement: &DatabasePlacement,
    ) -> Result<ProjectDatabaseInfo> {
        check_name("database", self.name())?;
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
            #[serde(flatten)]
            placement: &'a DatabasePlacement,
        }
        self.client()
            .management(
                Method::POST,
                vec!["v1", "databases"],
                &[],
                Some(&Body {
                    name: self.name(),
                    placement,
                }),
                StatusCode::CREATED,
            )
            .await
    }

    /// `GET D`; selectors accept a current name or immutable ULID.
    pub async fn get_database(&self) -> Result<ProjectDatabaseInfo> {
        self.management(Method::GET, &[], &[], None::<&()>, StatusCode::OK)
            .await
    }

    /// `PATCH D`; writable databases require at least two distinct serving regions.
    /// An empty replica list can temporarily disable document writes with HTTP 409
    /// `placement_needs_two_regions`. It does not provide supported single-region
    /// placement and is not required before deletion.
    pub async fn update_database(
        &self,
        placement: &DatabasePlacement,
    ) -> Result<ProjectDatabaseInfo> {
        self.management(Method::PATCH, &[], &[], Some(placement), StatusCode::OK)
            .await
    }

    /// `DELETE D` directly in the database's home region, without a placement update.
    /// Deletion covers every replica, including all-regions placement. HTTP 204
    /// confirms logical deletion and local cleanup; remote erasure is retried
    /// durably and may complete later.
    pub async fn delete_database(&self) -> Result<()> {
        check_database_selector(self.name())?;
        self.client()
            .send_project(Req::new(
                Method::DELETE,
                vec!["v1", "databases", self.name()],
            ))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }

    /// `POST D/branches`; independent snapshot copy in the same project.
    pub async fn branch_database(&self, name: &str) -> Result<ProjectDatabaseInfo> {
        check_name("database", name)?;
        self.management(
            Method::POST,
            &["branches"],
            &[],
            Some(&serde_json::json!({"name": name})),
            StatusCode::CREATED,
        )
        .await
    }

    pub(crate) async fn management<T: DeserializeOwned>(
        &self,
        method: Method,
        suffix: &[&str],
        query: &[(&str, String)],
        body: Option<&impl Serialize>,
        status: StatusCode,
    ) -> Result<T> {
        check_database_selector(self.name())?;
        let mut segments = vec!["v1", "databases", self.name()];
        segments.extend_from_slice(suffix);
        self.client()
            .management(method, segments, query, body, status)
            .await
    }
}
