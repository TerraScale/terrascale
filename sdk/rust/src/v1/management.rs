//! Canonical project management routes. Tooling bodies use the server's camelCase
//! JSON contract; deserialize into application structs or `serde_json::Value`.

use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};

use super::client::{Client, Req};
use super::database::{Database, ProjectDatabaseInfo};
use super::errors::Result;
use super::ids::{check_database_selector, check_name, is_valid_id};

/// Management pagination uses `after` / `next`, independently of data cursors.
#[derive(Debug, Clone, Deserialize)]
pub struct ManagementPage<T> {
    pub items: Vec<T>,
    pub next: Option<String>,
}

/// Complete region set or every-region placement; never combined with earlier placement fields.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabasePlacement {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub regions: Option<Vec<String>>,
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub all_regions: bool,
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

    /// Renames by immutable ULID or current name (`PATCH P/databases/{database_id}`).
    /// See docs/database-rename.md for producer availability and conflict handling.
    /// Requires `databases:write`. Returns the existing database metadata; its
    /// ID, data, keys and placement stay unchanged. Invalid names return 400
    /// (or local validation errors); known same-region duplicates return 409.
    /// After convergence the old name has no alias and may be claimed again.
    /// Update name-based connection strings and handles; ID-based connection strings remain valid. Reconcile uncertain
    /// failures before explicitly retrying this mutation.
    pub async fn rename_database(
        &self,
        database_id: &str,
        name: &str,
    ) -> Result<ProjectDatabaseInfo> {
        check_database_selector(database_id)?;
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
    /// `POST P/databases` with the complete selected region set or every-region placement.
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

    /// `DELETE D` in the database's home region. HTTP 204
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

    /// `POST D/branches/if-id/{expectedId}`; the server refuses with 409 before
    /// copying when the source name resolves to a different immutable ID.
    pub async fn branch_database_if_id(
        &self,
        name: &str,
        expected_id: &str,
    ) -> Result<ProjectDatabaseInfo> {
        check_name("database", name)?;
        check_expected_id(expected_id)?;
        self.management(
            Method::POST,
            &["branches", "if-id", expected_id],
            &[],
            Some(&serde_json::json!({"name": name})),
            StatusCode::CREATED,
        )
        .await
    }

    /// `DELETE D/if-id/{expectedId}`; the server refuses with 409 before any
    /// deletion work when the name resolves to a different immutable ID.
    pub async fn delete_database_if_id(&self, expected_id: &str) -> Result<()> {
        check_database_selector(self.name())?;
        check_expected_id(expected_id)?;
        self.client()
            .send_project(Req::new(
                Method::DELETE,
                vec!["v1", "databases", self.name(), "if-id", expected_id],
            ))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
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

fn check_expected_id(id: &str) -> Result<()> {
    if is_valid_id(id) {
        Ok(())
    } else {
        Err(super::errors::Error::InvalidInput(
            "expected database ID must be a canonical uppercase ULID".into(),
        ))
    }
}
