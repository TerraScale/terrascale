//! Backup management; restore acknowledgements differ from database reads.
use super::{
    client::{Client, Req},
    database::Database,
    errors::{Error, Result},
    ids::{check_database_selector, check_name, valid_entity_id},
};
use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use std::time::{Duration, Instant};

/// A background restore started by a restore route; `database` names the
/// restored database once `status` is `completed`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RestoreOperation {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub operation_id: String,
    pub status: String,
    pub kind: String,
    pub database: String,
    pub error_code: Option<String>,
    pub error_message: Option<String>,
    pub created_at: String,
    pub finished_at: Option<String>,
}

const RESTORE_POLL_INTERVAL: Duration = Duration::from_millis(250);
const RESTORE_DEADLINE: Duration = Duration::from_secs(600);

pub(crate) async fn await_restore_on<T: DeserializeOwned>(
    client: Client,
    source: &str,
    operation: RestoreOperation,
) -> Result<T> {
    Database::new(client, source.to_string())
        .await_restore(operation)
        .await
}

/// The producer's ManagedDatabase restore body has no serving `region` field.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RestoredDatabase {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub project_id: String,
    pub name: String,
    pub branched_from: Option<String>,
    pub created_at: String,
    pub home_region: Option<String>,
    #[serde(deserialize_with = "restore_placement_version")]
    pub placement_version: i64,
}
fn restore_placement_version<'de, D: serde::Deserializer<'de>>(
    d: D,
) -> std::result::Result<i64, D::Error> {
    let value = serde_json::Value::deserialize(d)?;
    let parsed = match value {
        serde_json::Value::Number(n) => n.as_i64(),
        serde_json::Value::String(s) => {
            let digits = s.strip_prefix('-').unwrap_or(&s);
            if !digits.is_empty()
                && (digits.len() == 1 || !digits.starts_with('0'))
                && digits.bytes().all(|b| b.is_ascii_digit())
            {
                s.parse::<i64>().ok()
            } else {
                None
            }
        }
        _ => None,
    };
    parsed.ok_or_else(|| serde::de::Error::custom("expected exact signed placement version"))
}
impl Client {
    /// `GET P/backup-destination`; responses never contain credentials.
    pub async fn get_backup_destination<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::GET,
            vec!["v1", "backup-destination"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `PUT P/backup-destination`; credentials are sent only in the body.
    pub async fn set_backup_destination<T: DeserializeOwned>(
        &self,
        body: &impl Serialize,
    ) -> Result<T> {
        self.management(
            Method::PUT,
            vec!["v1", "backup-destination"],
            &[],
            Some(body),
            StatusCode::OK,
        )
        .await
    }
    /// `DELETE P/backup-destination`.
    pub async fn delete_backup_destination(&self) -> Result<()> {
        self.send_project(Req::new(Method::DELETE, vec!["v1", "backup-destination"]))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }
}
impl Database {
    /// `GET D/restores/{operation}`; one background restore's status.
    pub async fn get_restore_operation(&self, operation: &str) -> Result<RestoreOperation> {
        if !valid_entity_id(operation) {
            return Err(Error::InvalidInput(
                "operation must be a canonical uppercase ULID".into(),
            ));
        }
        self.management(
            Method::GET,
            &["restores", operation],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }

    async fn await_restore<T: DeserializeOwned>(&self, operation: RestoreOperation) -> Result<T> {
        let mut operation = operation;
        let deadline = Instant::now() + RESTORE_DEADLINE;
        while operation.status == "running" {
            if Instant::now() >= deadline {
                return Err(Error::Api {
                    status: 504,
                    code: "restore_timeout".into(),
                    message: Some("the restore operation did not finish within ten minutes".into()),
                    details: None,
                });
            }
            tokio::time::sleep(RESTORE_POLL_INTERVAL).await;
            operation = self.get_restore_operation(&operation.id).await?;
        }
        if operation.status != "completed" {
            return Err(Error::Api {
                status: 200,
                code: operation
                    .error_code
                    .unwrap_or_else(|| "restore_failed".into()),
                message: Some(
                    operation
                        .error_message
                        .unwrap_or_else(|| "the restore operation failed".into()),
                ),
                details: None,
            });
        }
        Database::new(self.client().clone(), operation.database)
            .management::<T>(Method::GET, &[], &[], None::<&()>, StatusCode::OK)
            .await
    }

    /// `POST D/backups`; synchronous ready snapshot.
    pub async fn create_backup<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::POST,
            &["backups"],
            &[],
            None::<&()>,
            StatusCode::CREATED,
        )
        .await
    }
    /// `GET D/backups`; management after/next pagination.
    pub async fn list_backups<T: DeserializeOwned>(&self, query: &[(&str, String)]) -> Result<T> {
        self.management(
            Method::GET,
            &["backups"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `POST D/backups/{backup}/restore`; optional new database name. Waits for
    /// the background operation, then returns the restored database.
    pub async fn restore_backup(
        &self,
        backup: &str,
        name: Option<&str>,
    ) -> Result<RestoredDatabase> {
        if !valid_entity_id(backup) {
            return Err(Error::InvalidInput(
                "backup must be a canonical uppercase ULID".into(),
            ));
        }
        if let Some(name) = name {
            check_name("database", name)?;
        }
        let operation = self
            .management::<RestoreOperation>(
                Method::POST,
                &["backups", backup, "restore"],
                &[],
                Some(&serde_json::json!({"name":name})),
                StatusCode::ACCEPTED,
            )
            .await?;
        self.await_restore(operation).await
    }
    /// `GET D/backup-policy`.
    pub async fn get_backup_policy<T: DeserializeOwned>(&self) -> Result<T> {
        self.management(
            Method::GET,
            &["backup-policy"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `PUT D/backup-policy`; fullBackupCron, retentionDays and disabled.
    pub async fn set_backup_policy<T: DeserializeOwned>(&self, body: &impl Serialize) -> Result<T> {
        self.management(
            Method::PUT,
            &["backup-policy"],
            &[],
            Some(body),
            StatusCode::OK,
        )
        .await
    }
    /// `GET D/point-in-time`; optional destination=platform|customer.
    pub async fn get_restore_window<T: DeserializeOwned>(
        &self,
        query: &[(&str, String)],
    ) -> Result<T> {
        self.management(
            Method::GET,
            &["point-in-time"],
            query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// `POST D/point-in-time/plan`; read-only advisory metadata, never a restore.
    pub async fn plan_point_in_time<T: DeserializeOwned>(
        &self,
        body: &impl Serialize,
    ) -> Result<T> {
        check_database_selector(self.name())?;
        let mut req = Req::new(
            Method::POST,
            vec!["v1", "databases", self.name(), "point-in-time", "plan"],
        )
        .json(body)?;
        req.replay_safe = true;
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }
    /// `POST D/point-in-time/restore`; pointInTime, optional name/destination.
    /// Waits for the background operation, then returns the restored database.
    pub async fn restore_point_in_time(&self, body: &impl Serialize) -> Result<RestoredDatabase> {
        let operation = self
            .management::<RestoreOperation>(
                Method::POST,
                &["point-in-time", "restore"],
                &[],
                Some(body),
                StatusCode::ACCEPTED,
            )
            .await?;
        self.await_restore(operation).await
    }
}
