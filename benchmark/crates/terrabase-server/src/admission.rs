//! Authenticated host admission with durable, immutable replay records.
#[path = "admission_stop.rs"]
mod stop;
pub use stop::{
    PrepareStopReceipt, PrepareStopRequest, StopPreflightReceipt, StopReceipt, StopRequest,
    StoragePrepareStop, StorageStop, StorageStopPreflight,
};

use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs::{self, File, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};
use terrabase::{
    encryption::DatabaseKeyVersion,
    identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid},
    regions::RegionCode,
};

/// Immutable provisioning tuple sent by the local orchestrator.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct AdmissionRecord {
    /// Immutable creation time, Unix milliseconds, from the orchestrator admission journal.
    pub created_at: i64,
    /// Deployment environment.
    pub environment: String,
    /// Tenant identity.
    pub tenant_id: String,
    /// Project identity.
    pub project_id: String,
    /// Database identity.
    pub database_id: String,
    /// Canonical parent for a branch admission.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_database_id: Option<String>,
    /// Installed key version.
    pub key_version: String,
    /// Human readable database name.
    pub name: String,
    /// Immutable replication regions.
    pub regions: Vec<String>,
    /// Immutable residency policy.
    pub residency: String,
    /// Idempotency operation identity.
    pub operation_id: String,
    /// Target region.
    pub region: String,
    /// Target member identity.
    pub member_id: String,
}

/// Integration must acquire and use the installed OCI Vault lease when admitting storage.
pub type StorageAdmission = Arc<dyn Fn(&AdmissionRecord) -> Result<(), String> + Send + Sync>;

/// Serializes durable intents and replay before a host can accept requests.
pub struct AdmissionHost {
    root: PathBuf,
    environment: String,
    region: String,
    member_id: String,
    records: Mutex<BTreeMap<String, AdmissionRecord>>,
    admit_storage: StorageAdmission,
    identity_delivery: Option<Arc<crate::identity_secret_delivery::IdentitySecretDelivery>>,
    usage_delivery: Option<Arc<crate::usage_admission::UsageAdmissionJournal>>,
    stop_storage: Option<StorageStop>,
    prepare_stop_storage: Option<StoragePrepareStop>,
    preflight_stop_storage: Option<StorageStopPreflight>,
}

impl AdmissionHost {
    /// Installs durable billing stream registration on the same UID-authenticated listener.
    #[must_use]
    pub fn with_usage_delivery(
        mut self,
        delivery: Arc<crate::usage_admission::UsageAdmissionJournal>,
    ) -> Self {
        self.usage_delivery = Some(delivery);
        self
    }
    /// Installs fixed-file identity delivery on the UID-authenticated listener.
    #[must_use]
    pub fn with_identity_delivery(
        mut self,
        delivery: Arc<crate::identity_secret_delivery::IdentitySecretDelivery>,
    ) -> Self {
        self.identity_delivery = Some(delivery);
        self
    }
    /// Replays all durable intents through the encrypted storage integration.
    ///
    /// # Errors
    /// Refuses malformed manifests, identity conflicts, or failed storage replay.
    pub fn open(
        root: &Path,
        environment: String,
        region: RegionCode,
        member_id: MemberUlid,
        admit_storage: StorageAdmission,
    ) -> Result<Self, String> {
        let root = root.join("admissions");
        fs::create_dir_all(&root).map_err(|_| "admission directory unavailable")?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let metadata =
                fs::symlink_metadata(&root).map_err(|_| "admission directory unavailable")?;
            if !metadata.is_dir() || metadata.file_type().is_symlink() {
                return Err("unsafe admission directory".into());
            }
            fs::set_permissions(&root, fs::Permissions::from_mode(0o700))
                .map_err(|_| "admission permissions unavailable")?;
        }
        let host = Self {
            root,
            environment,
            region: region.to_string(),
            member_id: member_id.to_string(),
            records: Mutex::new(BTreeMap::new()),
            admit_storage,
            identity_delivery: None,
            usage_delivery: None,
            stop_storage: None,
            prepare_stop_storage: None,
            preflight_stop_storage: None,
        };
        for entry in fs::read_dir(&host.root).map_err(|_| "manifest unavailable")? {
            let entry = entry.map_err(|_| "manifest unavailable")?;
            if entry.path().extension().is_none_or(|extension| extension != "json") {
                continue;
            }
            let metadata =
                fs::symlink_metadata(entry.path()).map_err(|_| "manifest unavailable")?;
            if !metadata.is_file() || metadata.file_type().is_symlink() || metadata.len() > 16384 {
                return Err("unsafe admission manifest".into());
            }
            let record: AdmissionRecord = serde_json::from_slice(
                &fs::read(entry.path()).map_err(|_| "manifest unavailable")?,
            )
            .map_err(|_| "invalid admission manifest")?;
            host.validate(&record)?;
            if entry.file_name().to_str() != Some(&format!("{}.json", record.database_id)) {
                return Err("manifest identity mismatch".into());
            }
            (host.admit_storage)(&record)?;
            host.records
                .lock()
                .map_err(|_| "admission lock poisoned")?
                .insert(record.database_id.clone(), record);
        }
        Ok(host)
    }

    fn validate(&self, record: &AdmissionRecord) -> Result<(), String> {
        if record.created_at <= 0 {
            return Err("invalid admission creation time".into());
        }
        TenantUlid::from_text(&record.tenant_id).map_err(|_| "invalid tenant")?;
        ProjectUlid::from_text(&record.project_id).map_err(|_| "invalid project")?;
        DatabaseUlid::from_text(&record.database_id).map_err(|_| "invalid database")?;
        if let Some(parent) = &record.parent_database_id {
            DatabaseUlid::from_text(parent).map_err(|_| "invalid parent database")?;
            if parent == &record.database_id {
                return Err("branch cannot be its own parent".into());
            }
        }
        DatabaseKeyVersion::from_text(&record.key_version).map_err(|_| "invalid key version")?;
        if record.environment != self.environment
            || record.region != self.region
            || record.member_id != self.member_id
            || record.operation_id.is_empty()
            || record.operation_id.len() > 256
            || record.name.is_empty()
            || record.name.len() > 256
            || record.residency.is_empty()
            || record.residency.len() > 128
            || record.regions.is_empty()
            || record.regions.len() > 32
            || !record.regions.contains(&self.region)
        {
            return Err("invalid admission identity or placement".into());
        }
        let mut regions = std::collections::BTreeSet::new();
        let mut parsed_regions = Vec::with_capacity(record.regions.len());
        for region in &record.regions {
            let parsed = region.parse::<RegionCode>().map_err(|_| "invalid region")?;
            parsed_regions.push(parsed);
            if !regions.insert(region) {
                return Err("duplicate region".into());
            }
        }
        crate::control::validate_admission_residency(&record.residency, &parsed_regions)
            .map_err(|_| "residency violates placement")?;
        Ok(())
    }

    /// Persists an intent before storage admission; failed attempts remain replayable.
    ///
    /// # Errors
    /// Returns a refusal for conflicting retries or storage/durability failures.
    pub fn admit(&self, record: &AdmissionRecord) -> Result<(), String> {
        self.validate(record)?;
        let mut records = self.records.lock().map_err(|_| "admission lock poisoned")?;
        if let Some(existing) = records.get(&record.database_id) {
            if existing != record {
                return Err("immutable admission conflict".into());
            }
        } else {
            let path = self.root.join(format!("{}.json", record.database_id));
            let temporary = self.root.join(format!("{}.pending", record.database_id));
            let bytes = serde_json::to_vec(&record).map_err(|_| "manifest encoding failed")?;
            let mut options = OpenOptions::new();
            options.write(true).create_new(true);
            #[cfg(unix)]
            {
                use std::os::unix::fs::OpenOptionsExt;
                options.mode(0o600);
            }
            // A prior crash may leave an unpublished temporary file. Its content never grants admission.
            if temporary.exists() {
                fs::remove_file(&temporary).map_err(|_| "stale intent unavailable")?;
            }
            let mut file = options.open(&temporary).map_err(|_| "manifest unavailable")?;
            file.write_all(&bytes)
                .and_then(|()| file.sync_all())
                .map_err(|_| "manifest persistence failed")?;
            fs::rename(&temporary, &path).map_err(|_| "manifest publication failed")?;
            File::open(&self.root)
                .and_then(|directory| directory.sync_all())
                .map_err(|_| "manifest directory sync failed")?;
            records.insert(record.database_id.clone(), record.clone());
        }
        (self.admit_storage)(record)
    }
}

#[cfg(unix)]
impl AdmissionHost {
    /// Serves the private admission endpoint for one pinned orchestrator UID.
    /// The caller owns binding and unlinking the protected Unix socket.
    ///
    /// # Errors
    /// Returns the listener accept failure.
    pub async fn serve(
        self: Arc<Self>,
        listener: tokio::net::UnixListener,
        orchestrator_uid: u32,
    ) -> std::io::Result<()> {
        use axum::{
            Router,
            body::Body,
            extract::{DefaultBodyLimit, State},
            http::StatusCode,
            response::IntoResponse,
            routing::post,
        };
        async fn prepare_stop(
            State(host): State<Arc<AdmissionHost>>,
            body: axum::body::Bytes,
        ) -> axum::response::Response {
            let Ok(request) = serde_json::from_slice::<PrepareStopRequest>(&body) else {
                return StatusCode::BAD_REQUEST.into_response();
            };
            match tokio::task::spawn_blocking(move || host.prepare_stop(request)).await {
                Ok(Ok(receipt)) => axum::Json(receipt).into_response(),
                _ => StatusCode::CONFLICT.into_response(),
            }
        }
        async fn preflight_stop(
            State(host): State<Arc<AdmissionHost>>,
            body: axum::body::Bytes,
        ) -> axum::response::Response {
            let Ok(request) = serde_json::from_slice::<PrepareStopRequest>(&body) else {
                return StatusCode::BAD_REQUEST.into_response();
            };
            match tokio::task::spawn_blocking(move || host.preflight_stop(request)).await {
                Ok(Ok(receipt)) => axum::Json(receipt).into_response(),
                _ => StatusCode::CONFLICT.into_response(),
            }
        }
        async fn admit(
            State(host): State<Arc<AdmissionHost>>,
            body: axum::body::Bytes,
        ) -> axum::response::Response {
            let Ok(record) = serde_json::from_slice::<AdmissionRecord>(&body) else {
                return StatusCode::BAD_REQUEST.into_response();
            };
            let operation_id = record.operation_id.clone();
            let database_id = record.database_id.clone();
            match tokio::task::spawn_blocking(move || host.admit(&record)).await {
                Ok(Ok(())) => (StatusCode::OK, [("content-type", "application/json")], serde_json::json!({"operation_id": operation_id,"database_id": database_id,"state":"admitted"}).to_string()).into_response(),
                _ => StatusCode::CONFLICT.into_response(),
            }
        }
        async fn stop(
            State(host): State<Arc<AdmissionHost>>,
            body: axum::body::Bytes,
        ) -> axum::response::Response {
            let Ok(request) = serde_json::from_slice::<StopRequest>(&body) else {
                return StatusCode::BAD_REQUEST.into_response();
            };
            match tokio::task::spawn_blocking(move || host.stop(request)).await {
                Ok(Ok(receipt)) => axum::Json(receipt).into_response(),
                _ => StatusCode::CONFLICT.into_response(),
            }
        }
        if self.identity_delivery.is_some() && orchestrator_uid != 62010 {
            return Err(std::io::Error::other("identity delivery requires orchestrator UID"));
        }
        let identity_router =
            self.identity_delivery.as_ref().map(|delivery| delivery.clone().router());
        let usage_router =
            self.usage_delivery.clone().map(super::usage_admission::UsageAdmissionJournal::router);
        let router = Router::new()
            .route("/internal/v1/databases/admit", post(admit))
            .route("/internal/v1/databases/stop", post(stop))
            .route("/internal/v1/databases/delete/prepare", post(prepare_stop))
            .route("/internal/v1/databases/delete/preflight", post(preflight_stop))
            .layer(DefaultBodyLimit::max(16384))
            .with_state(self);
        let router =
            if let Some(identity) = identity_router { router.merge(identity) } else { router };
        let router = if let Some(usage) = usage_router { router.merge(usage) } else { router };
        loop {
            let (stream, _) = listener.accept().await?;
            if stream.peer_cred()?.uid() != orchestrator_uid {
                continue;
            }
            let router = router.clone();
            tokio::spawn(async move {
                let service = hyper::service::service_fn(
                    move |request: hyper::Request<hyper::body::Incoming>| {
                        let mut router = router.clone();
                        async move {
                            use tower_service::Service;
                            router.call(request.map(Body::new)).await
                        }
                    },
                );
                let _ = hyper::server::conn::http1::Builder::new()
                    .serve_connection(hyper_util::rt::TokioIo::new(stream), service)
                    .await;
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[test]
    fn durable_failed_intent_replays_and_rejects_mutated_scope() {
        let root = std::env::temp_dir().join(format!("admission-{}", DatabaseUlid::generate()));
        fs::create_dir(&root).unwrap();
        let member = MemberUlid::generate();
        let calls = Arc::new(AtomicUsize::new(0));
        let counter = calls.clone();
        let callback: StorageAdmission = Arc::new(move |_| {
            if counter.fetch_add(1, Ordering::SeqCst) == 0 {
                Err("key provider unavailable".into())
            } else {
                Ok(())
            }
        });
        let record = AdmissionRecord {
            created_at: 1_700_000_000_123,
            environment: "production".into(),
            tenant_id: TenantUlid::generate().to_string(),
            project_id: ProjectUlid::generate().to_string(),
            database_id: DatabaseUlid::generate().to_string(),
            parent_database_id: None,
            key_version: DatabaseKeyVersion::generate().to_string(),
            name: "orders".into(),
            regions: vec!["GRU".into()],
            residency: "GRU".into(),
            operation_id: "create-1".into(),
            region: "GRU".into(),
            member_id: member.to_string(),
        };
        let encoded = serde_json::to_value(&record).unwrap();
        assert_eq!(serde_json::from_value::<AdmissionRecord>(encoded.clone()).unwrap(), record);
        let mut missing = encoded.clone();
        missing.as_object_mut().unwrap().remove("created_at");
        assert!(serde_json::from_value::<AdmissionRecord>(missing).is_err());
        let mut string_timestamp = encoded;
        string_timestamp["created_at"] = serde_json::json!("1");
        assert!(serde_json::from_value::<AdmissionRecord>(string_timestamp).is_err());
        let host = AdmissionHost::open(
            &root,
            "production".into(),
            RegionCode::GRU,
            member,
            callback.clone(),
        )
        .unwrap();
        let mut zero_time = record.clone();
        zero_time.created_at = 0;
        assert!(host.admit(&zero_time).is_err());
        let mut negative_time = record.clone();
        negative_time.created_at = -1;
        assert!(host.admit(&negative_time).is_err());
        assert!(host.admit(&record).is_err());
        let mut invalid_residency = record.clone();
        invalid_residency.database_id = DatabaseUlid::generate().to_string();
        invalid_residency.residency = "eu-only".into();
        assert!(host.admit(&invalid_residency).is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        drop(host);
        let host =
            AdmissionHost::open(&root, "production".into(), RegionCode::GRU, member, callback)
                .unwrap();
        assert_eq!(calls.load(Ordering::SeqCst), 2);
        let mut conflict = record.clone();
        conflict.project_id = ProjectUlid::generate().to_string();
        assert!(host.admit(&conflict).is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 2);
        host.admit(&record).unwrap();
        let mut changed_time = record;
        changed_time.created_at += 1;
        assert!(host.admit(&changed_time).is_err());
        fs::remove_dir_all(root).unwrap();
    }
}
