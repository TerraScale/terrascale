//! Durable registration of lifecycle-approved billing streams.

use axum::{
    Json, Router, extract::DefaultBodyLimit, http::StatusCode, response::IntoResponse,
    routing::post,
};
use rusqlite::{Connection, OptionalExtension, TransactionBehavior, params};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeSet,
    path::Path,
    sync::{Arc, Mutex},
};
use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};
use terrabase::regions::RegionCode;
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_tbep::placement::PlacementJournal;

const MAX_BODY: usize = 16 * 1024;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct AdmissionRecord {
    pub schema_version: u32,
    pub environment: String,
    pub tenant_id: String,
    pub project_id: String,
    pub database_id: String,
    pub member_id: String,
    pub region: String,
    pub selected_regions: Vec<String>,
    pub residency: String,
    pub partition: String,
    pub stream_epoch: u64,
    pub placement_proof_digest: String,
    pub operation_id: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct AdmissionReceipt {
    pub schema_version: u32,
    pub environment: String,
    pub tenant_id: String,
    pub project_id: String,
    pub database_id: String,
    pub member_id: String,
    pub region: String,
    pub selected_regions: Vec<String>,
    pub residency: String,
    pub partition: String,
    pub stream_epoch: u64,
    pub placement_proof_digest: String,
    pub operation_id: String,
    pub state: &'static str,
}

pub struct UsageAdmissionJournal {
    root: std::path::PathBuf,
    environment: String,
    member_id: String,
    region: String,
    placement: Arc<PlacementJournal>,
    connection: Mutex<Connection>,
}

impl UsageAdmissionJournal {
    pub fn open(
        root: &Path,
        environment: String,
        member_id: String,
        region: String,
        placement: Arc<PlacementJournal>,
    ) -> Result<Arc<Self>, String> {
        if environment.is_empty() || member_id.is_empty() || region.parse::<RegionCode>().is_err() {
            return Err("invalid usage admission identity".into());
        }
        std::fs::create_dir_all(root).map_err(|_| "usage admission root unavailable")?;
        let path = root.join("usage-admissions.sqlite");
        let connection =
            Connection::open(&path).map_err(|_| "usage admission storage unavailable")?;
        connection
            .execute_batch(
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;",
            )
            .map_err(|_| "usage admission storage unavailable")?;
        connection.execute_batch("CREATE TABLE IF NOT EXISTS registrations (tenant_id TEXT NOT NULL, project_id TEXT NOT NULL, database_id TEXT NOT NULL, member_id TEXT NOT NULL, region TEXT NOT NULL, selected_regions TEXT NOT NULL, residency TEXT NOT NULL, partition TEXT NOT NULL, stream_epoch INTEGER NOT NULL, proof_digest TEXT NOT NULL, operation_id TEXT NOT NULL, PRIMARY KEY(tenant_id,project_id,database_id,member_id,region));").map_err(|_| "usage admission schema unavailable")?;
        Ok(Arc::new(Self {
            root: root.to_owned(),
            environment,
            member_id,
            region,
            placement,
            connection: Mutex::new(connection),
        }))
    }

    pub fn admit(&self, record: AdmissionRecord) -> Result<AdmissionReceipt, String> {
        self.validate(&record)?;
        let scope = DatabaseRouteScope::new(
            TenantUlid::from_text(&record.tenant_id).map_err(|_| "invalid tenant")?,
            ProjectUlid::from_text(&record.project_id).map_err(|_| "invalid project")?,
            DatabaseUlid::from_text(&record.database_id).map_err(|_| "invalid database")?,
        );
        let accepted = match self.placement.accepted() {
            Ok(accepted) => accepted,
            Err(terrabase_tbep::placement::PlacementError::NotFound) => {
                return Err("placement acceptance pending".into());
            }
            Err(error) => return Err(error.to_string()),
        };
        if accepted.proof_digest != record.placement_proof_digest {
            return Err("placement proof mismatch".into());
        }
        let accepted_proposal = self
            .placement
            .accepted_proposal()
            .map_err(|error| error.to_string())?
            ;
        if accepted.proposal_revision != accepted_proposal.revision {
            return Err("accepted placement revision changed".into());
        }
        let proposal = accepted_proposal
            .proposal
            .ok_or("accepted proposal missing")?;
        let assignment = self.placement.load_assignment(scope).map_err(|error| error.to_string())?;
        if assignment.epoch != record.stream_epoch
            || assignment.writer.member().as_str() != self.member_id
                && assignment.follower.member().as_str() != self.member_id
        {
            return Err("accepted assignment mismatch".into());
        }
        if proposal.epoch == 0
            || record
                .selected_regions
                .iter()
                .any(|region| !proposal.regions.iter().any(|seat| seat.region == *region))
        {
            return Err("accepted placement regions mismatch".into());
        }
        let selected = serde_json::to_string(&record.selected_regions)
            .map_err(|_| "invalid selected regions")?;
        let connection = self.connection.lock().map_err(|_| "usage admission lock poisoned")?;
        let tx = connection
            .unchecked_transaction()
            .map_err(|_| "usage admission transaction unavailable")?;
        let prior: Option<Vec<String>> = tx.query_row("SELECT selected_regions,residency,partition,stream_epoch,proof_digest,operation_id FROM registrations WHERE tenant_id=?1 AND project_id=?2 AND database_id=?3 AND member_id=?4 AND region=?5", params![record.tenant_id,record.project_id,record.database_id,record.member_id,record.region], |row| Ok(vec![row.get(0)?,row.get(1)?,row.get(2)?,row.get::<_,i64>(3)?.to_string(),row.get(4)?,row.get(5)?])).optional().map_err(|_| "usage admission read failed")?;
        if let Some(prior) = prior {
            let current = vec![
                selected.clone(),
                record.residency.clone(),
                record.partition.clone(),
                record.stream_epoch.to_string(),
                record.placement_proof_digest.clone(),
                record.operation_id.clone(),
            ];
            if prior != current {
                return Err("conflicting usage admission retry".into());
            }
        } else {
            tx.execute(
                "INSERT INTO registrations VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
                params![
                    record.tenant_id,
                    record.project_id,
                    record.database_id,
                    record.member_id,
                    record.region,
                    selected,
                    record.residency,
                    record.partition,
                    record.stream_epoch,
                    record.placement_proof_digest,
                    record.operation_id
                ],
            )
            .map_err(|_| "usage admission write failed")?;
            tx.commit().map_err(|_| "usage admission commit failed")?;
        }
        Ok(AdmissionReceipt {
            schema_version: record.schema_version,
            environment: record.environment,
            tenant_id: record.tenant_id,
            project_id: record.project_id,
            database_id: record.database_id,
            member_id: record.member_id,
            region: record.region,
            selected_regions: record.selected_regions,
            residency: record.residency,
            partition: record.partition,
            stream_epoch: record.stream_epoch,
            placement_proof_digest: record.placement_proof_digest,
            operation_id: record.operation_id,
            state: "registered",
        })
    }

    pub fn resolve(
        &self,
        record: &AdmissionRecord,
        epoch: u64,
        proof_digest: &str,
    ) -> Result<Option<String>, String> {
        let connection = self.connection.lock().map_err(|_| "usage admission lock poisoned")?;
        let selected = serde_json::to_string(&record.selected_regions)
            .map_err(|_| "invalid selected regions")?;
        connection.query_row("SELECT partition FROM registrations WHERE tenant_id=?1 AND project_id=?2 AND database_id=?3 AND member_id=?4 AND region=?5 AND selected_regions=?6 AND residency=?7 AND stream_epoch=?8 AND proof_digest=?9", params![record.tenant_id,record.project_id,record.database_id,record.member_id,record.region,selected,record.residency,epoch,proof_digest], |row| row.get(0)).optional().map_err(|_| "usage admission storage corrupt".into())
    }

    fn validate(&self, record: &AdmissionRecord) -> Result<(), String> {
        if record.schema_version != 1
            || record.environment != self.environment
            || record.member_id != self.member_id
            || record.region != self.region
            || record.stream_epoch == 0
            || record.operation_id.is_empty()
            || record.operation_id.len() > 256
            || record.partition.is_empty()
            || record.partition.len() > 255
            || !record
                .placement_proof_digest
                .bytes()
                .all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
        {
            return Err("invalid usage admission identity".into());
        }
        let mut regions = BTreeSet::new();
        for region in &record.selected_regions {
            region.parse::<RegionCode>().map_err(|_| "invalid selected region")?;
            if !regions.insert(region) {
                return Err("duplicate selected region".into());
            }
        }
        if regions.is_empty()
            || record.selected_regions != regions.iter().cloned().collect::<Vec<_>>()
            || !regions.contains(&self.region)
        {
            return Err("invalid selected regions".into());
        }
        Ok(())
    }
}

impl UsageAdmissionJournal {
    pub(crate) fn router(self: Arc<Self>) -> Router {
        Router::new()
            .route("/internal/v1/databases/usage/admit", post(admit))
            .layer(DefaultBodyLimit::max(MAX_BODY))
            .with_state(self)
    }
}

async fn admit(
    axum::extract::State(admission): axum::extract::State<Arc<UsageAdmissionJournal>>,
    Json(record): Json<AdmissionRecord>,
) -> impl IntoResponse {
    match tokio::task::spawn_blocking(move || admission.admit(record)).await {
        Ok(Ok(receipt)) => (StatusCode::OK, Json(receipt)).into_response(),
        _ => StatusCode::CONFLICT.into_response(),
    }
}
