use std::fmt;

use reqwest::{Method, StatusCode};
use serde::{Deserialize, Serialize};

use super::client::{Client, Req};
use super::database::ProjectDatabaseInfo;
use super::errors::{Error, Result};
use super::ids::{check_database_selector, check_name, is_valid_id};
use super::management::DatabasePlacement;

pub type ManagedDatabase = ProjectDatabaseInfo;

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateDatabaseOptions {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub regions: Option<Vec<String>>,
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub all_regions: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_seconds: Option<u64>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ForkDatabaseOptions {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub point_in_time: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_seconds: Option<u64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ScopedKeyAccess {
    Read,
    ReadWrite,
}

#[derive(Debug, Clone, Serialize)]
pub struct ScopedKeyOptions {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub project: Option<String>,
    pub database: String,
    pub access: ScopedKeyAccess,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_seconds: Option<u64>,
}

#[derive(Clone, Deserialize)]
pub struct ScopedKey {
    pub key: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub project_id: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub database: String,
    pub access: ScopedKeyAccess,
    pub expires_at: String,
}

impl fmt::Debug for ScopedKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ScopedKey")
            .field("key", &"<redacted>")
            .field("id", &self.id)
            .field("project_id", &self.project_id)
            .field("database", &self.database)
            .field("access", &self.access)
            .field("expires_at", &self.expires_at)
            .finish()
    }
}

fn check_regions(regions: &[String]) -> Result<()> {
    if regions.is_empty() || regions.len() > 9 {
        return Err(Error::InvalidInput(
            "regions must name one to nine regions".into(),
        ));
    }
    let mut seen = std::collections::BTreeSet::new();
    for region in regions {
        if region.is_empty() || !seen.insert(region) {
            return Err(Error::InvalidInput(
                "regions must be distinct region ids".into(),
            ));
        }
    }
    Ok(())
}

fn check_ttl(ttl: Option<u64>, maximum: u64) -> Result<()> {
    if ttl.is_some_and(|value| !(60..=maximum).contains(&value)) {
        return Err(Error::InvalidInput(format!(
            "ttl_seconds must be between 60 and {maximum}"
        )));
    }
    Ok(())
}

impl Client {
    pub async fn create_database(
        &self,
        name: &str,
        options: &CreateDatabaseOptions,
    ) -> Result<ManagedDatabase> {
        check_name("database", name)?;
        check_ttl(options.ttl_seconds, 31_536_000)?;
        if options.regions.is_some() && options.all_regions {
            return Err(Error::InvalidInput(
                "set regions or all_regions, not both".into(),
            ));
        }
        if let Some(regions) = &options.regions {
            check_regions(regions)?;
        }
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
            #[serde(flatten)]
            options: &'a CreateDatabaseOptions,
        }
        self.management(
            Method::POST,
            vec!["v1", "databases"],
            &[],
            Some(&Body { name, options }),
            StatusCode::CREATED,
        )
        .await
    }

    pub async fn fork_database(
        &self,
        source: &str,
        name: &str,
        options: &ForkDatabaseOptions,
    ) -> Result<ManagedDatabase> {
        check_database_selector(source)?;
        check_name("database", name)?;
        check_ttl(options.ttl_seconds, 31_536_000)?;
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
            #[serde(flatten)]
            options: &'a ForkDatabaseOptions,
        }
        let point_in_time = options.point_in_time.is_some();
        let mut segments = vec!["v1", "databases", source];
        if point_in_time {
            segments.extend(["point-in-time", "restore"]);
        } else {
            segments.push("branches");
        }
        let status = if point_in_time {
            StatusCode::ACCEPTED
        } else {
            StatusCode::CREATED
        };
        if !point_in_time {
            return self
                .management(
                    Method::POST,
                    segments,
                    &[],
                    Some(&Body { name, options }),
                    status,
                )
                .await;
        }
        let operation = self
            .management::<super::backups::RestoreOperation>(
                Method::POST,
                segments,
                &[],
                Some(&Body { name, options }),
                status,
            )
            .await?;
        super::backups::await_restore_on(self.clone(), source, operation).await
    }

    pub async fn set_database_ttl(
        &self,
        database: &str,
        ttl_seconds: u64,
    ) -> Result<ManagedDatabase> {
        check_ttl(Some(ttl_seconds), 31_536_000)?;
        self.database(database)
            .management(
                Method::PUT,
                &["ttl"],
                &[],
                Some(&serde_json::json!({"ttlSeconds": ttl_seconds})),
                StatusCode::OK,
            )
            .await
    }

    pub async fn set_database_placement(
        &self,
        database: &str,
        placement: &DatabasePlacement,
    ) -> Result<ManagedDatabase> {
        if placement.regions.is_some() == placement.all_regions {
            return Err(Error::InvalidInput(
                "set exactly one of regions or all_regions".into(),
            ));
        }
        if let Some(regions) = &placement.regions {
            check_regions(regions)?;
        }
        self.database(database)
            .management(Method::PATCH, &[], &[], Some(placement), StatusCode::OK)
            .await
    }

    pub async fn clear_database_ttl(&self, database: &str) -> Result<ManagedDatabase> {
        self.database(database)
            .management(Method::DELETE, &["ttl"], &[], None::<&()>, StatusCode::OK)
            .await
    }

    pub async fn delete_database(&self, database: &str) -> Result<()> {
        check_database_selector(database)?;
        self.send_project(Req::new(Method::DELETE, vec!["v1", "databases", database]))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }

    pub async fn create_scoped_key(&self, options: &ScopedKeyOptions) -> Result<ScopedKey> {
        if !is_valid_id(&options.database)
            || options
                .project
                .as_deref()
                .is_some_and(|id| !is_valid_id(id))
        {
            return Err(Error::InvalidInput(
                "scoped key database and project must be canonical uppercase ULIDs".into(),
            ));
        }
        check_ttl(options.ttl_seconds, 2_592_000)?;
        self.send_scoped_key(options)
            .await?
            .decode_status(&[StatusCode::CREATED])
    }
}
