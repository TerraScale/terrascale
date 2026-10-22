//! Canonical project management routes. Tooling bodies use the server's camelCase
//! JSON contract; deserialize into application structs or `serde_json::Value`.

use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};

use super::client::{Client, Req};
use super::documents::{Database, ProjectDatabaseInfo};
use super::errors::Result;
use super::ids::check_name;

/// Management pagination uses `after` / `next`, independently of data cursors.
#[derive(Debug, Clone, Deserialize)]
pub struct ManagementPage<T> {
    pub items: Vec<T>,
    pub next: Option<String>,
}

/// Placement supplied when creating or updating a database.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabasePlacement {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub home_region: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub replica_regions: Option<Vec<String>>,
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

    /// `GET D`; selectors are database names, response identities are ULIDs.
    pub async fn get_database(&self) -> Result<ProjectDatabaseInfo> {
        self.management(Method::GET, &[], &[], None::<&()>, StatusCode::OK)
            .await
    }

    /// `PATCH D`; an explicit empty replica list removes replicas.
    pub async fn update_database(
        &self,
        placement: &DatabasePlacement,
    ) -> Result<ProjectDatabaseInfo> {
        self.management(Method::PATCH, &[], &[], Some(placement), StatusCode::OK)
            .await
    }

    /// `DELETE D`; the server refuses replicated databases with 409.
    pub async fn delete_database(&self) -> Result<()> {
        check_name("database", self.name())?;
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
        check_name("database", self.name())?;
        let mut segments = vec!["v1", "databases", self.name()];
        segments.extend_from_slice(suffix);
        self.client()
            .management(method, segments, query, body, status)
            .await
    }
}
