//! Production lifecycle adapter seam.
//!
//! This handoff requires accepted-placement proof verification before native
//! mutation. Request epoch and mTLS scope are never treated as authority.

use std::sync::Arc;

use terrabase_tbep::lifecycle::LifecycleGrantAction;

/// Receipt of a completed native lifecycle effect.
pub enum NativeLifecycleReceipt {
    Snapshot(crate::native_recovery::RecoveryPointFacts),
    State(terrabase::engine::lifecycle_state::LifecycleOperationOutcome),
    Backup(terrabase::backup::worker::LifecycleReceipt),
}

/// Adapter installed on the TBEP server; it binds every request to the exact
/// accepted assignment before entering the native worker.
pub struct TbepLifecycleControl {
    control: Arc<ProductionLifecycleControl>,
}

/// Authority verifier backed by the member's retained placement journal.
pub struct PlacementLifecycleAuthority {
    journal: Arc<terrabase_tbep::placement::PlacementJournal>,
}

impl PlacementLifecycleAuthority {
    #[must_use]
    pub fn new(journal: Arc<terrabase_tbep::placement::PlacementJournal>) -> Self {
        Self { journal }
    }
}

impl LifecycleAuthority for PlacementLifecycleAuthority {
    fn verify(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
        expected_restore_digest: Option<&str>,
    ) -> Result<(), String> {
        self.journal
            .verify_lifecycle_authority(
                &assignment.environment,
                &assignment.tenant,
                &assignment.project,
                &assignment.database,
                &assignment.region,
                &assignment.member,
                assignment.placement_revision,
                assignment.authority_epoch,
                &assignment.authority_lease,
                signature,
            )
            .map_err(|_| "accepted lifecycle authority proof is invalid or stale")?;
        let grant = self
            .journal
            .verify_lifecycle_grant(operation_grant)
            .map_err(|_| "lifecycle operation grant is invalid")?;
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| "lifecycle operation clock is invalid")?
            .as_millis() as i64;
        grant.validate(now).map_err(|_| "lifecycle operation grant is expired or malformed")?;
        let digest = self
            .journal
            .accepted_proof_digest()
            .map_err(|_| "accepted lifecycle placement digest is unavailable")?;
        if grant.action != action
            || grant.operation_id != operation
            || (expected_restore_digest.is_some_and(|expected| grant.accepted_restore_request_digest != expected)
                || (expected_restore_digest.is_none() && !grant.accepted_restore_request_digest.is_empty()))
            || grant.environment != assignment.environment
            || grant.tenant != assignment.tenant
            || grant.project != assignment.project
            || grant.database != assignment.database
            || grant.region != assignment.region
            || grant.member != assignment.member
            || grant.accepted_placement_revision != assignment.placement_revision
            || grant.accepted_proof_digest != digest
            || grant.authority_epoch != assignment.authority_epoch
            || grant.authority_lease != assignment.authority_lease
        {
            return Err("lifecycle operation grant does not bind the accepted assignment".into());
        }
        self.journal
            .admit_lifecycle_operation(&grant)
            .map_err(|_| "lifecycle operation admission is stale or conflicting")?;
        Ok(())
    }
}

fn restore_request_digest(request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest) -> Result<String, String> {
    use aws_lc_rs::digest::{digest, SHA256};
    let mut bytes = Vec::new();
    for value in [&request.source_owner, &request.source_database, &request.recovery_point_id, &request.recovery_point_digest, &request.target_owner, &request.target_database, &request.journal_cut] {
        let len = u32::try_from(value.len()).map_err(|_| "restore request is too large")?;
        bytes.extend_from_slice(&len.to_be_bytes());
        bytes.extend_from_slice(value.as_bytes());
    }
    bytes.extend_from_slice(&request.restore_epoch.to_be_bytes());
    bytes.extend_from_slice(&(request.provider as u32).to_be_bytes());
    let mut input = b"terrascale-restore-request-v1\0".to_vec();
    input.extend_from_slice(&bytes);
    Ok(hex::encode(digest(&SHA256, &input).as_ref()))
}

impl TbepLifecycleControl {
    #[must_use]
    pub fn new(control: Arc<ProductionLifecycleControl>) -> Self {
        Self { control }
    }

    /// Restore dispatch is reserved for the native composition owner. The
    /// authenticated RPC reaches this boundary; no local path fallback is
    /// permitted while that owner is absent.
    pub async fn restore_to_target(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
    ) -> Result<terrabase_tbep::proto::terrabase::v1::LifecycleControlResponse, String> {
        let context = request.context.as_ref().ok_or("restore context is missing")?;
        if request.target_database != context.database
            || request.target_owner != format!("{}/{}", context.tenant, context.project) {
            return Err("restore target is outside the admitted lifecycle scope".into());
        }
        let assignment = Self::assignment(context);
        self.control.authority.verify(
            &assignment,
            LifecycleGrantAction::RestoreToTarget,
            &request.operation_id,
            &context.authority_signature,
            &context.operation_grant,
            Some(&restore_request_digest(request)?),
        )?;
        let _receipt = self.control.worker.restore_to_target(request).await?;
        Err("native restore admitted; retry for terminal verified facts".into())
/
    }

    fn assignment(
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
    ) -> AcceptedLifecycleAssignment {
        AcceptedLifecycleAssignment {
            environment: context.environment.clone(),
            tenant: context.tenant.clone(),
            project: context.project.clone(),
            database: context.database.clone(),
            region: context.region.clone(),
            member: context.member.clone(),
            placement_revision: context.placement_revision,
            authority_epoch: context.authority_epoch,
            authority_lease: context.authority_lease.clone(),
        }
    }

    async fn invoke(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
        action: LifecycleGrantAction,
    ) -> Result<terrabase_tbep::proto::terrabase::v1::LifecycleControlResponse, String> {
        let assignment = Self::assignment(context);
        let signature = &context.authority_signature;
        let grant = &context.operation_grant;
        match action {
            LifecycleGrantAction::EnsureSnapshot => {
                match self
                    .control
                    .ensure_snapshot(&assignment, action, operation, signature, grant)
                    .await?
                {
                    NativeLifecycleReceipt::Snapshot(facts) => {
                        Ok(terrabase_tbep::proto::terrabase::v1::LifecycleControlResponse {
                            status: 1,
                            operation_id: operation.to_owned(),
                            detail: String::new(),
                            recovery_point_id: facts.recovery_point_id,
                            recovery_point_digest: facts.checksum,
                            placement: facts.placement,
                        })
                    }
                    _ => Err("native snapshot receipt was not measured".into()),
                }
            }
            LifecycleGrantAction::VerifySnapshotPlacement => self
                .control
                .verify_snapshot_placement(&assignment, action, operation, signature, grant)
                .await
                .map(|_| terrabase_tbep::lifecycle::applied(operation)),
            LifecycleGrantAction::CloseDatabase => self
                .control
                .close_database(&assignment, action, operation, signature, grant)
                .await
                .map(|_| terrabase_tbep::lifecycle::applied(operation)),
            LifecycleGrantAction::StopNativeBackup => self
                .control
                .stop_native_backup(&assignment, action, operation, signature, grant)
                .await
                .map(|_| terrabase_tbep::lifecycle::applied(operation)),
            LifecycleGrantAction::Reactivate => self
                .control
                .reactivate(&assignment, action, operation, signature, grant)
                .await
                .map(|_| terrabase_tbep::lifecycle::applied(operation)),
            LifecycleGrantAction::DeleteOwnedData => self
                .control
                .delete_owned_data(&assignment, action, operation, signature, grant)
                .await
                .map(|_| terrabase_tbep::lifecycle::applied(operation)),
            LifecycleGrantAction::RestoreToTarget => {
                Err("restore requires its typed request".into())
            }
        }
    }
}

impl terrabase_tbep::lifecycle::LifecycleControl for TbepLifecycleControl {
    fn restore_to_target(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let request = request.clone();
        Box::pin(async move { self.restore_to_target(&request).await })
    }
    fn ensure_snapshot(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let context = context.clone();
        let operation = operation.to_owned();
        Box::pin(async move {
            self.invoke(&context, &operation, LifecycleGrantAction::EnsureSnapshot).await
        })
    }
    fn verify_snapshot_placement(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let context = context.clone();
        let operation = operation.to_owned();
        Box::pin(async move {
            self.invoke(&context, &operation, LifecycleGrantAction::VerifySnapshotPlacement).await
        })
    }
    fn close_database(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let context = context.clone();
        let operation = operation.to_owned();
        Box::pin(async move {
            self.invoke(&context, &operation, LifecycleGrantAction::CloseDatabase).await
        })
    }
    fn stop_native_backup(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let context = context.clone();
        let operation = operation.to_owned();
        Box::pin(async move {
            self.invoke(&context, &operation, LifecycleGrantAction::StopNativeBackup).await
        })
    }
    fn reactivate(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let context = context.clone();
        let operation = operation.to_owned();
        Box::pin(async move {
            self.invoke(&context, &operation, LifecycleGrantAction::Reactivate).await
        })
    }
    fn delete_owned_data(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
    ) -> terrabase_tbep::lifecycle::LifecycleFuture<'_> {
        let context = context.clone();
        let operation = operation.to_owned();
        Box::pin(async move {
            self.invoke(&context, &operation, LifecycleGrantAction::DeleteOwnedData).await
        })
    }
}

/// Exact assignment retained by the durable placement journal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AcceptedLifecycleAssignment {
    /// Deployment environment.
    pub environment: String,
    /// Tenant identity.
    pub tenant: String,
    /// Project identity.
    pub project: String,
    /// Database identity.
    pub database: String,
    /// Selected region.
    pub region: String,
    /// Selected member.
    pub member: String,
    /// Accepted journal revision.
    pub placement_revision: u64,
    /// Accepted authority epoch.
    pub authority_epoch: u64,
    /// Accepted authority lease.
    pub authority_lease: String,
}

/// Native lifecycle owner supplied by member and backup composition.
#[async_trait::async_trait]
pub trait NativeLifecycleWorker: Send + Sync {
    /// Admits an exact native restore operation into the durable recovery owner.
    async fn restore_to_target(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
    ) -> Result<NativeLifecycleReceipt, String> {
        let _ = request;
        Err("native restore owner is not composed".into())
    }
    /// Ensures all selected destinations contain a complete snapshot receipt.
    async fn ensure_snapshot(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Closes serving while retaining durable data.
    async fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Stops only this database's native backup stream.
    async fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Resumes the same retained database without replacing its identity.
    async fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Verifies the native placement without changing serving state.
    async fn verify_snapshot_placement(
        &self,
        _assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<(), String> {
        Err("native snapshot placement verifier is unavailable".into())
    }
    /// Deletes data owned by this member after durable authority admission.
    async fn delete_owned_data(
        &self,
        _assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<(), String> {
        Err("native owned-data deletion is unavailable".into())
    }
}

/// Native worker backed by the admitted database registry.
///
/// This supplies the reversible pause/resume portion; snapshot and backup
/// receipt work still requires the configured fleet owner.
pub struct RegistryLifecycleWorker {
    /// Durable admitted database registry.
    pub registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
    /// Optional native backup fleet retained by the production composition.
    pub backups: Option<Arc<std::sync::Mutex<terrabase::backup::worker::NativeBackupFleet>>>,
    /// Durable native recovery owner.
    pub recovery: Option<Arc<crate::native_recovery::NativeRecoveryRuntime>>,
}

#[async_trait::async_trait]
impl NativeLifecycleWorker for RegistryLifecycleWorker {
    async fn restore_to_target(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
    ) -> Result<NativeLifecycleReceipt, String> {
        let recovery = self.recovery.as_ref().ok_or("native recovery owner is not composed")?;
        let operation = crate::native_recovery::NativeRestoreRequest {
            operation_id: request.operation_id.clone(),
            source_owner: request.source_owner.clone(),
            source_database_id: request.source_database.clone(),
            recovery_point_id: request.recovery_point_id.clone(),
            recovery_point_digest: request.recovery_point_digest.clone(),
            target_owner: request.target_owner.clone(),
            target_database_id: request.target_database.clone(),
            journal_cut: request.journal_cut.clone(),
            restore_epoch: request.restore_epoch,
            provider: match terrabase_tbep::proto::terrabase::v1::RestoreProvider::try_from(request.provider) {
                Ok(terrabase_tbep::proto::terrabase::v1::RestoreProvider::Oci) => "oci",
                Ok(terrabase_tbep::proto::terrabase::v1::RestoreProvider::Tigris) => "tigris",
                _ => return Err("restore provider is not approved".into()),
            }
            .into(),
            selection: crate::native_recovery::NativeRestoreSelection::PointId(
                request.recovery_point_id.clone(),
            ),
            placement: vec![
                request.context.as_ref().ok_or("restore context is missing")?.region.clone(),
            ],
        };
        recovery
            .admit_restore(operation)
            .map(|_| Err("native restore admitted; retry for terminal verified facts".into()))
            .map_err(|_| "native restore admission refused".into())
    }

    async fn ensure_snapshot(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String> {
        let recovery = self.recovery.as_ref().ok_or("native recovery owner is not composed")?;
        let result = recovery
            .capture(assignment.database.clone(), operation.to_owned())
            .await
            .map_err(|_| "native recovery capture unavailable")?;
        let facts = result.facts.ok_or("native recovery capture is not complete")?;
        Ok(NativeLifecycleReceipt::Snapshot(facts))
    }

    async fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<NativeLifecycleReceipt, String> {
        self.registry.pause(&assignment.database, _operation).map(NativeLifecycleReceipt::State)
    }

    async fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<NativeLifecycleReceipt, String> {
        let backups = self.backups.as_ref().ok_or("native backup fleet is not attached")?;
        backups
            .lock()
            .map_err(|_| "native backup fleet poisoned")?
            .pause(&assignment.database)
            .map(|_| {
                NativeLifecycleReceipt::Backup(terrabase::backup::worker::LifecycleReceipt {
                    operation_id: _operation.to_owned(),
                    database: assignment.database.clone(),
                    action: "stop_native_backup".into(),
                    point_id: String::new(),
                    point_digest: String::new(),
                })
            })
            .map_err(|_| "native backup pause refused".into())
    }

    async fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<NativeLifecycleReceipt, String> {
        self.registry.resume(&assignment.database, _operation).map(NativeLifecycleReceipt::State)
    }
}

/// Authority verifier for signed accepted placement proofs.
pub trait LifecycleAuthority: Send + Sync {
    /// Verifies proof against currently retained accepted state.
    fn verify(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
        expected_restore_digest: Option<&str>,
    ) -> Result<(), String>;
}

/// Adapter that verifies authority immediately before every mutation.
pub struct ProductionLifecycleControl {
    /// Native owner.
    pub worker: Arc<dyn NativeLifecycleWorker>,
    /// Accepted-placement authority.
    pub authority: Arc<dyn LifecycleAuthority>,
}

impl ProductionLifecycleControl {
    /// Ensures a complete snapshot after verifying accepted authority.
    pub async fn ensure_snapshot(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<NativeLifecycleReceipt, String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.ensure_snapshot(assignment, operation).await
    }

    /// Closes serving after verifying accepted authority.
    pub async fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<(), String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.close_database(assignment, operation).await
    }

    /// Stops one native backup stream after verifying accepted authority.
    pub async fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<NativeLifecycleReceipt, String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.stop_native_backup(assignment, operation).await
    }

    /// Verifies accepted authority before resuming the same retained database.
    pub async fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<NativeLifecycleReceipt, String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.reactivate(assignment, operation).await
    }

    pub async fn verify_snapshot_placement(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<NativeLifecycleReceipt, String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.verify_snapshot_placement(assignment, operation).await.map(|_| {
            NativeLifecycleReceipt::State(
                terrabase::engine::lifecycle_state::LifecycleOperationOutcome::AlreadyApplied,
            )
        })
    }

    pub async fn delete_owned_data(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<(), String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.delete_owned_data(assignment, operation).await
    }
}
