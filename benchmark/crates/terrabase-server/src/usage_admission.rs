//! Durable billing-stream registrations on the authenticated local control socket.
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, State},
    http::StatusCode,
    response::IntoResponse,
    routing::post,
};
use rusqlite::{Connection, OpenFlags, OptionalExtension, TransactionBehavior, params};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeSet,
    path::Path,
    sync::{Arc, Mutex},
};
fn unix_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_millis().try_into().unwrap_or(u64::MAX))
}
use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase::regions::RegionCode;
use terrabase_cluster::{production::DatabaseRouteScope, wal_route::DurableWalAssignmentSource};
use terrabase_tbep::placement::PlacementJournal;

/// Immutable lifecycle-approved stream bound to signed accepted placement.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct UsageRegistration {
    /// Wire version, currently one.
    pub schema_version: u32,
    /// Deployment environment.
    pub environment: String,
    /// Canonical tenant.
    pub tenant_id: String,
    /// Canonical project.
    pub project_id: String,
    /// Canonical database.
    pub database_id: String,
    /// Receiving member.
    pub member_id: String,
    /// Local stream origin.
    pub region: String,
    /// Full database Region selection, sorted without duplicates.
    pub selected_regions: Vec<String>,
    /// Immutable named residency policy.
    pub residency: String,
    /// Partition allocated by the durable billing lifecycle authority.
    pub partition: String,
    /// Accepted writer epoch.
    pub stream_epoch: u64,
    /// Digest of the signed accepted placement proof.
    pub placement_proof_digest: String,
    /// Stable control operation identity.
    pub operation_id: String,
}

/// Runtime-owned identity used to bind a delivered allocation to accepted
/// local placement. Implementations must fail closed when the database is not
/// currently admitted.
pub trait AllocationBindingResolver: Send + Sync {
    /// Returns organization, holder, placement revision, and writer epoch.
    fn binding(&self, database_id: &str) -> Result<(String, String, String, u64), String>;
}

/// Explicit command-signature trust configuration for budget delivery.
#[derive(Clone)]
pub struct AllocationVerifierConfig {
    /// Lowercase SHA-256 SPKI key identity.
    pub key_id: String,
    /// Pinned ECDSA P-256 SubjectPublicKeyInfo bytes.
    pub public_key_spki: Vec<u8>,
    /// Current policy epoch.
    pub policy_epoch: u64,
    /// Current consent epoch.
    pub consent_epoch: u64,
    /// Runtime-owned accepted placement and writer identity resolver.
    pub resolver: Arc<dyn AllocationBindingResolver>,
}
/// Durable acknowledgment repeating the exact registration.
#[derive(Debug, Serialize)]
pub struct AdmissionReceipt {
    /// Persisted registration fields.
    #[serde(flatten)]
    pub registration: UsageRegistration,
    /// Successful durable registration state.
    pub state: &'static str,
}
/// Private `SQLite` control metadata; never customer records or invented billing authority.
pub struct UsageAdmissionJournal {
    environment: String,
    member_id: String,
    region: String,
    placement: Arc<PlacementJournal>,
    connection: Mutex<Connection>,
    operations: Arc<tokio::sync::Semaphore>,
    allocation_verifier: Mutex<Option<AllocationVerifierConfig>>,
}
fn private(path: &Path, directory: bool) -> Result<(), String> {
    let m = std::fs::symlink_metadata(path).map_err(|_| "usage admission path unavailable")?;
    if m.file_type().is_symlink() || m.is_dir() != directory || (!directory && !m.is_file()) {
        return Err("unsafe usage admission path".into());
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt, PermissionsExt};
        if m.uid() != rustix::process::geteuid().as_raw()
            || m.permissions().mode() & 0o077 != 0
            || (!directory && m.nlink() != 1)
        {
            return Err("usage admission storage must be private to its owner".into());
        }
    }
    Ok(())
}
impl UsageAdmissionJournal {
    /// Opens a journal bound permanently to this environment and local member.
    /// # Errors
    /// Refuses unsafe files, changed identity, and unavailable durable storage.
    pub fn open(
        root: &Path,
        environment: String,
        member_id: String,
        region: String,
        placement: Arc<PlacementJournal>,
    ) -> Result<Self, String> {
        terrabase::member::EnvironmentName::try_new(&environment)
            .map_err(|_| "invalid usage environment")?;
        MemberUlid::from_text(&member_id).map_err(|_| "invalid usage member")?;
        region.parse::<RegionCode>().map_err(|_| "invalid usage Region")?;
        private(root, true)?;
        let path = root.join("usage-admissions.sqlite");
        for file in [
            path.clone(),
            root.join("usage-admissions.sqlite-wal"),
            root.join("usage-admissions.sqlite-shm"),
        ] {
            match std::fs::symlink_metadata(&file) {
                Ok(_) => private(&file, false)?,
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(_) => return Err("usage admission storage unavailable".into()),
            }
        }
        let mut options = std::fs::OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.mode(0o600);
        }
        match options.open(&path) {
            Ok(file) => {
                file.sync_all().map_err(|_| "usage admission file sync failed")?;
                std::fs::File::open(root)
                    .and_then(|f| f.sync_all())
                    .map_err(|_| "usage admission directory sync failed")?;
            }
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(_) => return Err("usage admission storage unavailable".into()),
        }
        private(&path, false)?;
        let mut connection = Connection::open_with_flags(
            &path,
            OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_NOFOLLOW,
        )
        .map_err(|_| "usage admission storage unavailable")?;
        connection
            .busy_timeout(std::time::Duration::from_secs(5))
            .map_err(|_| "usage admission timeout unavailable")?;
        connection.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS identity (singleton INTEGER PRIMARY KEY CHECK(singleton=1), environment TEXT NOT NULL, member TEXT NOT NULL, region TEXT NOT NULL) STRICT;
            CREATE TABLE IF NOT EXISTS registrations (database_id TEXT NOT NULL, epoch TEXT NOT NULL, payload BLOB NOT NULL, PRIMARY KEY(database_id,epoch)) STRICT, WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS allocations (operation TEXT PRIMARY KEY, database_id TEXT NOT NULL, payload BLOB NOT NULL) STRICT, WITHOUT ROWID;").map_err(|_| "usage admission schema unavailable")?;
        let tx = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| "usage admission transaction unavailable")?;
        tx.execute(
            "INSERT OR IGNORE INTO identity VALUES(1,?1,?2,?3)",
            params![environment, member_id, region],
        )
        .map_err(|_| "usage admission identity unavailable")?;
        let identity: (String, String, String) = tx
            .query_row(
                "SELECT environment,member,region FROM identity WHERE singleton=1",
                [],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .map_err(|_| "usage admission identity unavailable")?;
        if identity != (environment.clone(), member_id.clone(), region.clone()) {
            return Err("usage admission journal identity mismatch".into());
        }
        tx.commit().map_err(|_| "usage admission identity sync failed")?;
        Ok(Self {
            environment,
            member_id,
            region,
            placement,
            connection: Mutex::new(connection),
            operations: Arc::new(tokio::sync::Semaphore::new(16)),
            allocation_verifier: Mutex::new(None),
        })
    }

    /// Installs the owner-provided pinned budget verifier before the listener
    /// is composed. An absent verifier keeps allocation delivery closed.
    pub fn with_allocation_verifier(
        self,
        config: AllocationVerifierConfig,
    ) -> Result<Self, String> {
        if config.key_id.is_empty()
            || config.public_key_spki.is_empty()
            || config.policy_epoch == 0
            || config.consent_epoch == 0
        {
            return Err("budget verifier configuration is invalid".into());
        }
        *self.allocation_verifier.lock().map_err(|_| "budget verifier lock unavailable")? =
            Some(config);
        Ok(self)
    }
    /// Verifies and durably installs one signed regional allocation.
    pub fn admit_allocation(
        &self,
        signed_bytes: &[u8],
    ) -> Result<terrabase::usage::VerifiedAllocation, String> {
        let config = self
            .allocation_verifier
            .lock()
            .map_err(|_| "budget verifier lock unavailable")?
            .clone()
            .ok_or("budget verifier unavailable")?;
        let wire: terrabase::usage::SignedAllocation =
            serde_json::from_slice(signed_bytes).map_err(|_| "allocation encoding invalid")?;
        let envelope: terrabase::usage::AllocationEnvelope =
            serde_json::from_slice(&wire.payload).map_err(|_| "allocation payload invalid")?;
        let (organization, holder, placement, writer_epoch) =
            config.resolver.binding(&envelope.grant.database_id)?;
        let verified = terrabase::usage::VerifiedAllocation::verify(
            signed_bytes,
            &config.key_id,
            &config.public_key_spki,
            &self.region,
            &placement,
            &organization,
            &envelope.grant.database_id,
            &holder,
            config.policy_epoch,
            config.consent_epoch,
            writer_epoch,
            unix_millis(),
        )
        .map_err(|_| "allocation verification failed")?;
        if verified.grant().writer_epoch != writer_epoch {
            return Err("allocation writer epoch mismatch".into());
        }
        let mut connection =
            self.connection.lock().map_err(|_| "usage admission lock unavailable")?;
        let tx = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| "usage admission transaction unavailable")?;
        tx.execute(
            "INSERT OR IGNORE INTO allocations(operation,database_id,payload) VALUES(?1,?2,?3)",
            params![verified.grant().operation_id, verified.grant().database_id, signed_bytes],
        )
        .map_err(|_| "allocation persistence failed")?;
        let stored: Vec<u8> = tx
            .query_row(
                "SELECT payload FROM allocations WHERE operation=?1",
                [&verified.grant().operation_id],
                |row| row.get(0),
            )
            .map_err(|_| "allocation persistence failed")?;
        let stored_wire: terrabase::usage::SignedAllocation =
            serde_json::from_slice(&stored).map_err(|_| "allocation persistence failed")?;
        // ECDSA signatures are randomized. Retries with the same canonical
        // payload retain the operation identity even when the wrapper
        // signature differs; the incoming wrapper was fully re-verified above.
        if !same_allocation_identity(&stored_wire, &wire) {
            return Err("allocation identity conflict".into());
        }
        tx.commit().map_err(|_| "allocation persistence failed")?;
        Ok(verified)
    }

    /// Reloads the exact persisted allocation for one operation and verifies
    /// it against the current runtime binding. A missing row is a closed
    /// billing stream; stale rows are rejected by the same epoch/expiry
    /// checks used at delivery time.
    pub fn resolve_allocation(
        &self,
        operation_id: &str,
        database_id: &str,
    ) -> Result<Option<terrabase::usage::VerifiedAllocation>, String> {
        if operation_id.is_empty() || database_id.is_empty() {
            return Err("allocation identity is invalid".into());
        }
        let config = self
            .allocation_verifier
            .lock()
            .map_err(|_| "budget verifier lock unavailable")?
            .clone()
            .ok_or("budget verifier unavailable")?;
        let connection = self.connection.lock().map_err(|_| "usage admission lock unavailable")?;
        let signed: Option<Vec<u8>> = connection
            .query_row(
                "SELECT payload FROM allocations WHERE operation=?1 AND database_id=?2",
                params![operation_id, database_id],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| "allocation persistence failed")?;
        let Some(signed) = signed else { return Ok(None) };
        let envelope: terrabase::usage::AllocationEnvelope = serde_json::from_slice(
            &serde_json::from_slice::<terrabase::usage::SignedAllocation>(&signed)
                .map_err(|_| "allocation encoding invalid")?
                .payload,
        )
        .map_err(|_| "allocation payload invalid")?;
        if envelope.grant.operation_id != operation_id || envelope.grant.database_id != database_id
        {
            return Err("allocation identity conflict".into());
        }
        let (organization, holder, placement, writer_epoch) =
            config.resolver.binding(database_id)?;
        let verified = terrabase::usage::VerifiedAllocation::verify(
            &signed,
            &config.key_id,
            &config.public_key_spki,
            &self.region,
            &placement,
            &organization,
            database_id,
            &holder,
            config.policy_epoch,
            config.consent_epoch,
            writer_epoch,
            unix_millis(),
        )
        .map_err(|_| "allocation verification failed")?;
        Ok(Some(verified))
    }

    fn validate(&self, r: &UsageRegistration) -> Result<(), String> {
        TenantUlid::from_text(&r.tenant_id).map_err(|_| "invalid usage tenant")?;
        ProjectUlid::from_text(&r.project_id).map_err(|_| "invalid usage project")?;
        DatabaseUlid::from_text(&r.database_id).map_err(|_| "invalid usage database")?;
        let regions = r.selected_regions.iter().cloned().collect::<BTreeSet<_>>();
        if r.schema_version != 1
            || r.environment != self.environment
            || r.member_id != self.member_id
            || r.region != self.region
            || r.stream_epoch == 0
            || r.operation_id.is_empty()
            || r.operation_id.len() > 256
            || r.partition.is_empty()
            || r.partition.len() > 255
            || r.partition.chars().any(char::is_control)
            || r.residency.is_empty()
            || r.residency.len() > 128
            || r.placement_proof_digest.len() != 64
            || !r
                .placement_proof_digest
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            || regions.is_empty()
            || regions.len() > 20
            || !regions.contains(&self.region)
            || regions.into_iter().collect::<Vec<_>>() != r.selected_regions
            || r.selected_regions.iter().any(|v| v.parse::<RegionCode>().is_err())
        {
            return Err("invalid usage admission identity or placement".into());
        }
        Ok(())
    }
    fn decode(
        &self,
        database: &str,
        epoch: &str,
        bytes: &[u8],
    ) -> Result<UsageRegistration, String> {
        if bytes.len() > 16384 {
            return Err("usage admission row exceeds bound".into());
        }
        let r: UsageRegistration =
            serde_json::from_slice(bytes).map_err(|_| "usage admission row corrupt")?;
        self.validate(&r)?;
        if r.database_id != database || format!("{:020}", r.stream_epoch) != epoch {
            return Err("usage admission row key mismatch".into());
        }
        Ok(r)
    }
    /// Persists a lifecycle registration after independent verification of accepted placement.
    /// # Errors
    /// Refuses unaccepted placement, changed immutable scope, conflicting retries, or failed persistence.
    pub fn admit(&self, r: UsageRegistration) -> Result<AdmissionReceipt, String> {
        self.validate(&r)?;
        let accepted =
            self.placement.accepted().map_err(|_| "usage placement acceptance unavailable")?;
        let proposal = self
            .placement
            .accepted_proposal()
            .map_err(|_| "usage accepted proposal unavailable")?;
        if accepted.proof_digest != r.placement_proof_digest
            || accepted.proposal_revision != proposal.revision
        {
            return Err("usage accepted proof mismatch".into());
        }
        let proposal = proposal.proposal.ok_or("usage accepted proposal missing")?;
        let assigned = proposal
            .assignments
            .iter()
            .filter(|a| {
                a.tenant == r.tenant_id && a.project == r.project_id && a.database == r.database_id
            })
            .collect::<Vec<_>>();
        let regions = assigned.iter().map(|a| a.region.clone()).collect::<BTreeSet<_>>();
        let local = assigned
            .iter()
            .find(|a| a.region == r.region)
            .ok_or("usage local assignment missing")?;
        if proposal.environment != r.environment
            || proposal.epoch != r.stream_epoch
            || regions.into_iter().collect::<Vec<_>>() != r.selected_regions
            || (local.writer_member != self.member_id && local.follower_member != self.member_id)
        {
            return Err("usage accepted assignment mismatch".into());
        }
        let scope = DatabaseRouteScope::try_new(&r.tenant_id, &r.project_id, &r.database_id)
            .map_err(|_| "usage scope invalid")?;
        let assignment =
            self.placement.load_assignment(scope).map_err(|_| "usage assignment unavailable")?;
        if assignment.epoch != r.stream_epoch
            || assignment.writer.member().as_str() != local.writer_member
            || assignment.follower.member().as_str() != local.follower_member
        {
            return Err("usage accepted assignment changed".into());
        }
        self.persist(r)
    }
    fn persist(&self, r: UsageRegistration) -> Result<AdmissionReceipt, String> {
        let epoch = format!("{:020}", r.stream_epoch);
        let bytes = serde_json::to_vec(&r).map_err(|_| "usage admission encoding failed")?;
        if bytes.len() > 16384 {
            return Err("usage admission exceeds bound".into());
        }
        let mut connection =
            self.connection.lock().map_err(|_| "usage admission lock unavailable")?;
        let tx = connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| "usage admission transaction unavailable")?;
        let latest:Option<(String,Vec<u8>)>=tx.query_row("SELECT epoch,substr(payload,1,16385) FROM registrations WHERE database_id=?1 ORDER BY epoch DESC LIMIT 1",[&r.database_id],|row| Ok((row.get(0)?,row.get(1)?))).optional().map_err(|_| "usage admission read failed")?;
        if let Some((prior_epoch, payload)) = latest {
            let prior = self.decode(&r.database_id, &prior_epoch, &payload)?;
            if prior.tenant_id != r.tenant_id
                || prior.project_id != r.project_id
                || prior.selected_regions != r.selected_regions
                || prior.residency != r.residency
                || prior.partition != r.partition
            {
                return Err("usage admission immutable scope conflict".into());
            }
            if prior.stream_epoch > r.stream_epoch {
                return Err("usage admission stale epoch".into());
            }
            if prior.stream_epoch == r.stream_epoch {
                if prior != r {
                    return Err("usage admission conflicting retry".into());
                }
                return Ok(AdmissionReceipt { registration: r, state: "registered" });
            }
        }
        tx.execute(
            "INSERT INTO registrations VALUES(?1,?2,?3)",
            params![r.database_id, epoch, bytes],
        )
        .map_err(|_| "usage admission write failed")?;
        tx.commit().map_err(|_| "usage admission commit failed")?;
        Ok(AdmissionReceipt { registration: r, state: "registered" })
    }
    /// Resolves an exact persisted stream; absence leaves storage closed pending private delivery.
    /// # Errors
    /// Refuses corrupted rows or mismatched host, owner, residency, and proof.
    pub fn resolve(
        &self,
        r: &crate::admission::AdmissionRecord,
        epoch: u64,
        digest: &str,
    ) -> Result<Option<String>, String> {
        if r.environment != self.environment
            || r.member_id != self.member_id
            || r.region != self.region
            || epoch == 0
        {
            return Err("usage admission host mismatch".into());
        }
        let connection = self.connection.lock().map_err(|_| "usage admission lock unavailable")?;
        let epoch_key = format!("{epoch:020}");
        let bytes: Option<Vec<u8>> = connection
            .query_row(
                "SELECT substr(payload,1,16385) FROM registrations WHERE database_id=?1 AND epoch=?2",
                params![r.database_id, epoch_key],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| "usage admission read failed")?;
        let Some(bytes) = bytes else {
            return Ok(None);
        };
        let stored = self.decode(&r.database_id, &epoch_key, &bytes)?;
        let mut regions = r.regions.clone();
        regions.sort();
        if stored.tenant_id != r.tenant_id
            || stored.project_id != r.project_id
            || stored.selected_regions != regions
            || stored.residency != r.residency
            || stored.placement_proof_digest != digest
        {
            return Err("usage admission tuple mismatch".into());
        }
        Ok(Some(stored.partition))
    }
    pub(crate) fn router(self: Arc<Self>) -> Router {
        Router::new()
            .route("/internal/v1/databases/usage/admit", post(admit))
            .route("/internal/v1/databases/usage/allocation", post(admit_allocation))
            .layer(DefaultBodyLimit::max(16384))
            .with_state(self)
    }
}

fn same_allocation_identity(
    stored: &terrabase::usage::SignedAllocation,
    incoming: &terrabase::usage::SignedAllocation,
) -> bool {
    stored.key_id == incoming.key_id && stored.payload == incoming.payload
}
async fn admit_allocation(
    State(journal): State<Arc<UsageAdmissionJournal>>,
    Json(record): Json<terrabase::usage::SignedAllocation>,
) -> impl IntoResponse {
    let Ok(permit) = journal.operations.clone().try_acquire_owned() else {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    };
    match tokio::task::spawn_blocking(move || {
        let _permit = permit;
        serde_json::to_vec(&record)
            .map_err(|_| "allocation encoding failed".to_owned())
            .and_then(|bytes| journal.admit_allocation(&bytes))
    })
    .await
    {
        Ok(Ok(_)) => StatusCode::OK.into_response(),
        _ => StatusCode::CONFLICT.into_response(),
    }
}
async fn admit(
    State(journal): State<Arc<UsageAdmissionJournal>>,
    Json(record): Json<UsageRegistration>,
) -> impl IntoResponse {
    let Ok(permit) = journal.operations.clone().try_acquire_owned() else {
        return StatusCode::SERVICE_UNAVAILABLE.into_response();
    };
    match tokio::task::spawn_blocking(move || {
        let _permit = permit;
        journal.admit(record)
    })
    .await
    {
        Ok(Ok(receipt)) => (StatusCode::OK, Json(receipt)).into_response(),
        _ => StatusCode::CONFLICT.into_response(),
    }
}
#[cfg(test)]
mod tests;
