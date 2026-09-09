//! Production lifecycle adapter seam.
//!
//! This handoff requires accepted-placement proof verification before native
//! mutation. Request epoch and mTLS scope are never treated as authority.

use std::sync::Arc;

use terrabase_tbep::lifecycle::LifecycleGrantAction;

/// Receipt of a completed native lifecycle effect.
pub enum NativeLifecycleReceipt {
    /// Verified recovery point facts.
    Snapshot(crate::native_recovery::RecoveryPointFacts),
    /// Completed database lifecycle transition.
    State(terrabase::engine::lifecycle_state::LifecycleOperationOutcome),
    /// Completed native backup operation.
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
    /// Creates a verifier over the retained placement journal.
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
        verify_restore_request_digest(&grant, expected_restore_digest)?;
        if grant.action != action
            || grant.operation_id != operation
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

fn verify_restore_request_digest(
    grant: &terrabase_tbep::lifecycle::LifecycleOperationGrant,
    expected: Option<&str>,
) -> Result<(), String> {
    match expected {
        Some(expected) if grant.accepted_restore_request_digest == expected => Ok(()),
        Some(_) => Err("lifecycle restore request digest does not match".into()),
        None if grant.accepted_restore_request_digest.is_empty() => Ok(()),
        None => Err("non-restore lifecycle grant carries restore request digest".into()),
    }
}

pub(crate) fn restore_request_digest(
    request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
) -> Result<String, String> {
    use aws_lc_rs::digest::{SHA256, digest};
    let mut bytes = Vec::new();
    for value in [
        &request.source_owner,
        &request.source_database,
        &request.recovery_point_id,
        &request.recovery_point_digest,
        &request.target_owner,
        &request.target_database,
        &request.journal_cut,
    ] {
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
    /// Creates the authenticated TBEP lifecycle adapter.
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
            || request.target_owner != format!("{}/{}", context.tenant, context.project)
        {
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
        let operation = self.control.worker.restore_to_target(request).await?;
        use crate::native_recovery::NativeRestorePhase;
        use terrabase_tbep::proto::terrabase::v1::{LifecycleControlResponse, LifecycleStatus};
        match operation.phase {
            NativeRestorePhase::Complete => {
                let signed_completed_receipt = self
                    .control
                    .worker
                    .signed_completed_receipt(&operation.request.operation_id)
                    .await?;
                let receipt =
                    operation.receipt.ok_or("complete restore has no verified receipt")?;
                Ok(LifecycleControlResponse {
                    status: LifecycleStatus::Applied as i32,
                    operation_id: operation.request.operation_id,
                    detail: String::new(),
                    signed_completed_receipt,
                    recovery_point_id: operation.request.recovery_point_id,
                    recovery_point_digest: receipt.selected_identity,
                    placement: operation.request.placement,
                })
            }
            NativeRestorePhase::Failed => Err("native restore failed".into()),
            NativeRestorePhase::Accepted
            | NativeRestorePhase::Restoring
            | NativeRestorePhase::Verifying
            | NativeRestorePhase::Uncertain => Ok(LifecycleControlResponse {
                status: LifecycleStatus::Accepted as i32,
                operation_id: operation.request.operation_id,
                detail: String::new(),
                signed_completed_receipt: Vec::new(),
                recovery_point_id: String::new(),
                recovery_point_digest: String::new(),
                placement: Vec::new(),
            }),
        }
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
                            recovery_point_id: facts.recovery_point_id.clone(),
                            // The wire digest is the authenticated backup
                            // manifest identity. Logical content checksum is
                            // returned by RestoreStats after restore.
                            recovery_point_digest: facts.recovery_point_id.clone(),
                            placement: facts.placement,
                            signed_completed_receipt: Vec::new(),
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
            LifecycleGrantAction::ResolveRecoveryPoint => {
                Err("resolve requires its typed request".into())
            }
        }
    }
}

impl terrabase_tbep::lifecycle::LifecycleControl for TbepLifecycleControl {
    fn resolve_recovery_point(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointRequest,
    ) -> terrabase_tbep::lifecycle::ResolveRecoveryPointFuture<'_> {
        let request = request.clone();
        Box::pin(async move { self.resolve_recovery_point(&request).await })
    }

    fn restore_stats(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreStatsRequest,
    ) -> terrabase_tbep::lifecycle::RestoreStatsFuture<'_> {
        let request = request.clone();
        Box::pin(async move { self.control.restore_stats(&request).await })
    }
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
    /// Resolves one retained, sealed recovery manifest under the accepted source scope.
    async fn resolve_recovery_point(
        &self,
        _assignment: &AcceptedLifecycleAssignment,
        _request: &terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointRequest,
    ) -> Result<terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointResponse, String> {
        Err("native recovery point resolver is not composed".into())
    }

    /// Signs only durable completed observations under the explicitly provisioned key.
    async fn signed_completed_receipt(&self, _operation: &str) -> Result<Vec<u8>, String> {
        Err("native receipt signer is not composed".into())
    }
    /// Reads complete verified restore facts for an exact target identity.
    async fn restore_stats(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreStatsRequest,
    ) -> Result<crate::native_recovery::NativeRestoreReceipt, String> {
        let _ = request;
        Err("native restore stats owner is not composed".into())
    }
    /// Returns the digest and full placement of the durably recorded restore request.
    async fn restore_request_facts(
        &self,
        _operation: &str,
        _target: &str,
        _owner: &str,
    ) -> Result<(String, Vec<String>), String> {
        Err("native restore request is unavailable".into())
    }
    /// Admits an exact native restore operation into the durable recovery owner.
    async fn restore_to_target(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
    ) -> Result<crate::native_recovery::NativeRestoreOperation, String> {
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
    /// Shared async gate for every native fleet operation.
    pub backup_gate: Option<Arc<tokio::sync::Mutex<()>>>,
    /// Durable native recovery owner.
    pub recovery: Option<Arc<crate::native_recovery::NativeRecoveryRuntime>>,
    /// Durable signed completed observations.
    pub receipts: Arc<crate::native_recovery_receipt::NativeRecoveryReceiptStore>,
    /// Retained placement journal used to decode the already verified grant.
    pub journal: Option<Arc<terrabase_tbep::placement::PlacementJournal>>,
}

#[async_trait::async_trait]
impl NativeLifecycleWorker for RegistryLifecycleWorker {
    async fn resolve_recovery_point(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        request: &terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointRequest,
    ) -> Result<terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointResponse, String> {
        if request.operation_id.is_empty() || request.operation_id.len() > 128 {
            return Err("recovery point operation identity is invalid".into());
        }
        let recovery = self.recovery.as_ref().ok_or("native recovery owner is not composed")?;
        let database = self
            .registry
            .resolve(&assignment.database)?
            .ok_or("recovery source is not admitted")?;
        if database.record.environment != assignment.environment
            || database.record.tenant_id != assignment.tenant
            || database.record.project_id != assignment.project
            || database.record.database_id != assignment.database
            || database.record.region != assignment.region
            || database.record.member_id != assignment.member
            || !database.record.regions.iter().any(|region| region == &assignment.region)
        {
            return Err("recovery source scope or placement mismatch".into());
        }
        let envelope = recovery
            .resolve_recovery_envelope(
                &assignment.database,
                &request.selection_kind,
                &request.point_id,
                &database.record.regions,
            )
            .await
            .map_err(|_| "retained recovery point is unavailable or unverified")?;
        let (
            source_database,
            recovery_point_id,
            journal_cut,
            captured_at_millis,
            checksum,
            row_count,
            provider,
            envelope_facts,
        ) = if let Some((envelope, provider)) = envelope {
            let journal_cut = envelope
                .participants()
                .iter()
                .map(|participant| participant.boundary)
                .max()
                .unwrap_or_default();
            (
                envelope.database_id().to_owned(),
                envelope.digest().to_owned(),
                journal_cut.to_string(),
                envelope.captured_unix_millis(),
                envelope.logical_checksum(),
                envelope.logical_documents(),
                provider,
                Some(terrabase_tbep::proto::terrabase::v1::RecoveryEnvelopeFacts {
                    envelope_digest: envelope.digest().to_owned(),
                    database_id: envelope.database_id().to_owned(),
                    database_incarnation: format!("{:032x}", envelope.database_incarnation()),
                    route_generation: envelope.route_generation(),
                    participants: envelope
                        .participants()
                        .iter()
                        .map(|participant| {
                            terrabase_tbep::proto::terrabase::v1::RecoveryParticipant {
                                participant_id: participant.participant_id.clone(),
                                database_id: participant.database_id.clone(),
                                journal_cut: participant.boundary,
                                manifest_digest: participant.manifest.identity().to_owned(),
                                checksum: participant.manifest.logical_checksum().to_owned(),
                                row_count: participant.manifest.logical_documents(),
                                captured_at_millis: participant.manifest.captured_unix_millis(),
                                retained_until_millis: participant
                                    .manifest
                                    .retain_until_unix_millis(),
                            }
                        })
                        .collect(),
                }),
            )
        } else {
            let (manifest, provider) = recovery
                .resolve_recovery_point(
                    &assignment.database,
                    &request.selection_kind,
                    &request.point_id,
                    &database.record.regions,
                )
                .await
                .map_err(|_| "retained recovery point is unavailable or unverified")?;
            (
                manifest.database_id().to_owned(),
                manifest.identity().to_owned(),
                manifest.boundary_sequence().get().to_string(),
                manifest.captured_unix_millis(),
                manifest.logical_checksum().to_owned(),
                manifest.logical_documents(),
                provider,
                None,
            )
        };
        let provider = match provider {
            terrabase::backup::object_store::Provider::Oci => {
                terrabase_tbep::proto::terrabase::v1::RestoreProvider::Oci
            }
            terrabase::backup::object_store::Provider::Tigris => {
                terrabase_tbep::proto::terrabase::v1::RestoreProvider::Tigris
            }
        };
        Ok(terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointResponse {
            operation_id: request.operation_id.clone(),
            source_database,
            recovery_point_id: recovery_point_id.clone(),
            recovery_point_digest: recovery_point_id,
            journal_cut,
            provider: provider as i32,
            captured_at_millis,
            checksum,
            row_count,
            placement: database.record.regions.clone(),
            envelope: envelope_facts,
        })
    }

    async fn signed_completed_receipt(&self, operation: &str) -> Result<Vec<u8>, String> {
        let recovery = self.recovery.as_ref().ok_or("native recovery owner unavailable")?;
        let operation = recovery
            .restore_status(operation)
            .map_err(|_| "native restore observation unavailable")?
            .ok_or("native restore observation absent")?;
        if operation.phase != crate::native_recovery::NativeRestorePhase::Complete {
            return Err("native restore is not complete".into());
        }
        let source = recovery
            .verified_restore_source_facts(&operation.request)
            .await
            .map_err(|_| "authenticated restore source facts unavailable")?;
        self.receipts
            .completed(&operation, &source)
            .map_err(|_| "native restore signed observation unavailable".into())
    }

    async fn restore_stats(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreStatsRequest,
    ) -> Result<crate::native_recovery::NativeRestoreReceipt, String> {
        let recovery =
            self.recovery.as_ref().ok_or("native restore stats owner is not composed")?;
        recovery
            .verified_restore_stats(
                &request.operation_id,
                &request.target_database,
                &request.target_owner,
                &request.journal_cut,
                request.restore_epoch,
            )
            .map_err(|_| "native restore stats unavailable".to_owned())?
            .ok_or_else(|| "native restore is not complete or identity mismatched".to_owned())
    }
    async fn restore_request_facts(
        &self,
        operation: &str,
        target: &str,
        owner: &str,
    ) -> Result<(String, Vec<String>), String> {
        let recovery = self.recovery.as_ref().ok_or("native restore owner is not composed")?;
        let request = recovery
            .restore_request(operation, target, owner)
            .map_err(|_| "native restore request unavailable")?
            .ok_or("native restore request not found")?;
        let provider = if request.provider == "oci" {
            1
        } else if request.provider == "tigris" {
            2
        } else {
            return Err("restore provider is not approved".into());
        };
        let wire = terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest {
            operation_id: request.operation_id,
            source_owner: request.source_owner,
            source_database: request.source_database_id,
            recovery_point_id: request.recovery_point_id,
            recovery_point_digest: request.recovery_point_digest,
            target_owner: request.target_owner,
            target_database: request.target_database_id,
            journal_cut: request.journal_cut,
            restore_epoch: request.restore_epoch,
            provider,
            ..Default::default()
        };
        Ok((restore_request_digest(&wire)?, request.placement))
    }
    async fn restore_to_target(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest,
    ) -> Result<crate::native_recovery::NativeRestoreOperation, String> {
        let recovery = self.recovery.as_ref().ok_or("native recovery owner is not composed")?;
        // Exact durable retries must remain observable after the source moves.
        // Admission below compares every immutable field, including placement.
        let retained = recovery
            .restore_request(&request.operation_id, &request.target_database, &request.target_owner)
            .map_err(|_| "native restore request mismatch")?;
        let (placement, residency) = if let Some(retained) = &retained {
            (retained.placement.clone(), retained.residency.clone())
        } else {
            let source = self
                .registry
                .resolve(&request.source_database)?
                .ok_or("restore source is not admitted")?;
            if format!("{}/{}", source.record.tenant_id, source.record.project_id)
                != request.source_owner
            {
                return Err("restore source owner mismatch".into());
            }
            (source.record.regions.clone(), source.record.residency.clone())
        };
        let target = self
            .registry
            .retained_snapshot()?
            .into_iter()
            .find(|entry| entry.database.record.database_id == request.target_database)
            .ok_or("restore target is not retained")?;
        if target.database.record.regions != placement
            || target.database.record.residency != residency
            || format!("{}/{}", target.database.record.tenant_id, target.database.record.project_id)
                != request.target_owner
        {
            return Err("restore target placement or owner mismatch".into());
        }
        let (authority_epoch, authority_lease, accepted_proof_digest, operation_grant) =
            if let Some(retained) = retained.as_ref() {
                (
                    retained.authority_epoch,
                    retained.authority_lease.clone(),
                    retained.accepted_proof_digest.clone(),
                    retained.operation_grant.clone(),
                )
            } else {
                let context = request.context.as_ref().ok_or("restore context is missing")?;
                let proof_digest = self
                    .journal
                    .as_ref()
                    .ok_or("placement authority is unavailable")?
                    .verify_lifecycle_grant(&context.operation_grant)
                    .map_err(|_| "lifecycle operation grant is invalid")?
                    .accepted_proof_digest;
                (
                    context.authority_epoch,
                    context.authority_lease.clone(),
                    proof_digest,
                    context.operation_grant.clone(),
                )
            };
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
            authority_epoch,
            authority_lease,
            accepted_proof_digest,
            operation_grant,
            provider: match terrabase_tbep::proto::terrabase::v1::RestoreProvider::try_from(
                request.provider,
            ) {
                Ok(terrabase_tbep::proto::terrabase::v1::RestoreProvider::Oci) => "oci",
                Ok(terrabase_tbep::proto::terrabase::v1::RestoreProvider::Tigris) => "tigris",
                _ => return Err("restore provider is not approved".into()),
            }
            .into(),
            selection: crate::native_recovery::NativeRestoreSelection::PointId(
                request.recovery_point_id.clone(),
            ),
            residency,
            placement,
        };
        let admitted = recovery
            .admit_restore(operation)
            .map_err(|_| "native restore admission refused".to_owned())?;
        if matches!(admitted.phase, crate::native_recovery::NativeRestorePhase::Uncertain) {
            recovery
                .retry_restore_readback(&request.operation_id)
                .map_err(|_| "native restore readback unavailable".into())
        } else {
            Ok(admitted)
        }
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

    async fn delete_owned_data(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<(), String> {
        let database = self
            .registry
            .resolve_lifecycle(&assignment.database)?
            .ok_or("database is not retained")?;
        let tenant = terrabase::types::TenantId::try_new(&database.record.tenant_id)
            .map_err(|_| "invalid admitted tenant")?;
        let state = database
            .host
            .engine()
            .lifecycle_state(&database.host.engine().scope_for(&tenant))
            .map_err(|_| "lifecycle state unavailable")?;
        let intent = state.deletion.as_ref().ok_or("durable deletion intent is missing")?;
        let retain_until = intent.retain_until.ok_or("deletion retention cutoff is unknown")?;
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| "system clock unavailable")?
            .as_millis() as u64;
        if !state.access_revoked() || retain_until > now {
            return Err("durable deletion retention boundary has not elapsed".into());
        }
        let backups =
            Arc::clone(self.backups.as_ref().ok_or("native backup fleet is not composed")?);
        let backup_gate = Arc::clone(
            self.backup_gate.as_ref().ok_or("native backup fleet gate is not composed")?,
        );
        let database_id = assignment.database.clone();
        let operation = operation.to_owned();
        // The blocking owner retains fleet serialization throughout provider I/O;
        // the async request only awaits a Send join handle.
        tokio::task::spawn_blocking(move || {
            let _gate = tokio::runtime::Handle::current().block_on(backup_gate.lock());
            let mut fleet = backups.lock().map_err(|_| "native backup fleet poisoned")?;
            tokio::runtime::Handle::current()
                .block_on(fleet.lifecycle_delete(&database_id, &operation))
                .map_err(|_| "provider-owned backup deletion is pending")
        })
        .await
        .map_err(|_| "native backup deletion worker unavailable")??;
        Ok(())
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
    /// Resolves a retained source point only after accepted lifecycle authority verification.
    pub async fn resolve_recovery_point(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointRequest,
    ) -> Result<terrabase_tbep::proto::terrabase::v1::ResolveRecoveryPointResponse, String> {
        let context = request.context.as_ref().ok_or("recovery point context is missing")?;
        let assignment = TbepLifecycleControl::assignment(context);
        self.authority.verify(
            &assignment,
            LifecycleGrantAction::ResolveRecoveryPoint,
            &request.operation_id,
            &context.authority_signature,
            &context.operation_grant,
            None,
        )?;
        self.worker.resolve_recovery_point(&assignment, request).await
    }

    /// Returns durable restore progress for the authorized request.
    pub async fn restore_stats(
        &self,
        request: &terrabase_tbep::proto::terrabase::v1::RestoreStatsRequest,
    ) -> Result<terrabase_tbep::proto::terrabase::v1::RestoreStatsResponse, String> {
        let context = request.context.as_ref().ok_or("restore stats context is missing")?;
        let assignment = TbepLifecycleControl::assignment(context);
        let (request_digest, placement) = self
            .worker
            .restore_request_facts(
                &request.operation_id,
                &request.target_database,
                &request.target_owner,
            )
            .await?;
        self.authority.verify(
            &assignment,
            LifecycleGrantAction::RestoreToTarget,
            &request.operation_id,
            &context.authority_signature,
            &context.operation_grant,
            Some(&request_digest),
        )?;
        let receipt = self.worker.restore_stats(request).await?;
        let signed_completed_receipt =
            self.worker.signed_completed_receipt(&request.operation_id).await?;
        Ok(terrabase_tbep::proto::terrabase::v1::RestoreStatsResponse {
            operation_id: request.operation_id.clone(),
            target_owner: request.target_owner.clone(),
            target_database: request.target_database.clone(),
            signed_completed_receipt,
            checksum: receipt.logical_content_checksum,
            row_count: i64::try_from(receipt.restored_documents)
                .map_err(|_| "restore row count exceeds the wire range")?,
            journal_cut: request.journal_cut.clone(),
            placement,
            envelope: None,
        })
    }
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
    ) -> Result<NativeLifecycleReceipt, String> {
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

    /// Verifies snapshot authority against accepted placement.
    pub async fn verify_snapshot_placement(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        action: LifecycleGrantAction,
        operation: &str,
        signature: &[u8],
        operation_grant: &[u8],
    ) -> Result<(), String> {
        self.authority.verify(assignment, action, operation, signature, operation_grant, None)?;
        self.worker.verify_snapshot_placement(assignment, operation).await
    }

    /// Deletes owned data after validating lifecycle authority.
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

#[cfg(test)]
mod restore_digest_tests {
    use super::{restore_request_digest, verify_restore_request_digest};
    use terrabase_tbep::lifecycle::{LifecycleGrantAction, LifecycleOperationGrant};
    use terrabase_tbep::proto::terrabase::v1::{RestoreProvider, RestoreToTargetRequest};

    #[test]
    fn restore_request_digest_matches_cross_language_vector() {
        let request = RestoreToTargetRequest {
            source_owner: "source-owner".into(),
            source_database: "source-database".into(),
            recovery_point_id: "recovery-point".into(),
            recovery_point_digest: "recovery-digest".into(),
            target_owner: "target-owner".into(),
            target_database: "target-database".into(),
            journal_cut: "journal-cut".into(),
            restore_epoch: 7,
            provider: RestoreProvider::Oci as i32,
            ..Default::default()
        };
        assert_eq!(
            restore_request_digest(&request).expect("digest"),
            "8de75d48c6ed3bba6d0b05f266c390a3fb36c155ba423a225e8e733fae64413d"
        );
        let mut altered = request.clone();
        altered.source_owner = "other-owner".into();
        assert_ne!(
            restore_request_digest(&altered).unwrap(),
            restore_request_digest(&request).unwrap()
        );
        let mut altered = request.clone();
        altered.recovery_point_digest = "other-digest".into();
        assert_ne!(
            restore_request_digest(&altered).unwrap(),
            restore_request_digest(&request).unwrap()
        );
        let mut altered = request.clone();
        altered.target_database = "other-target".into();
        assert_ne!(
            restore_request_digest(&altered).unwrap(),
            restore_request_digest(&request).unwrap()
        );
        let mut altered = request.clone();
        altered.journal_cut = "other-cut".into();
        assert_ne!(
            restore_request_digest(&altered).unwrap(),
            restore_request_digest(&request).unwrap()
        );
        let mut altered = request.clone();
        altered.restore_epoch = 8;
        assert_ne!(
            restore_request_digest(&altered).unwrap(),
            restore_request_digest(&request).unwrap()
        );
        let mut altered = request;
        altered.provider = RestoreProvider::Tigris as i32;
        assert_ne!(
            restore_request_digest(&altered).unwrap(),
            restore_request_digest(&RestoreToTargetRequest {
                provider: RestoreProvider::Oci as i32,
                source_owner: "source-owner".into(),
                source_database: "source-database".into(),
                recovery_point_id: "recovery-point".into(),
                recovery_point_digest: "recovery-digest".into(),
                target_owner: "target-owner".into(),
                target_database: "target-database".into(),
                journal_cut: "journal-cut".into(),
                restore_epoch: 7,
                ..Default::default()
            })
            .unwrap()
        );
    }

    #[test]
    fn lifecycle_verifier_binds_restore_digest_and_rejects_non_restore_digest() {
        let expected = "a".repeat(64);
        let mut grant = LifecycleOperationGrant {
            version: 1,
            domain: "terrascale-lifecycle-operation-v1".into(),
            environment: "production".into(),
            tenant: "tenant".into(),
            project: "project".into(),
            database: "database".into(),
            region: "GRU".into(),
            member: "member".into(),
            action: LifecycleGrantAction::RestoreToTarget,
            operation_id: "op".into(),
            accepted_proof_digest: "b".repeat(64),
            accepted_placement_revision: 1,
            authority_epoch: 1,
            authority_lease: "lease".into(),
            accepted_restore_request_digest: expected.clone(),
            issued_at_unix_millis: 1,
            expires_at_unix_millis: 2,
        };
        assert!(verify_restore_request_digest(&grant, Some(&expected)).is_ok());
        grant.accepted_restore_request_digest = "c".repeat(64);
        assert!(verify_restore_request_digest(&grant, Some(&expected)).is_err());
        grant.action = LifecycleGrantAction::CloseDatabase;
        assert!(verify_restore_request_digest(&grant, None).is_err());
    }
}

#[cfg(test)]
mod restore_stats_tests {
    use super::*;
    use crate::native_recovery::NativeRestoreReceipt;
    use terrabase_tbep::proto::terrabase::v1::{LifecycleControlContext, RestoreStatsRequest};

    struct Worker {
        complete: bool,
    }
    #[async_trait::async_trait]
    impl NativeLifecycleWorker for Worker {
        async fn signed_completed_receipt(&self, _: &str) -> Result<Vec<u8>, String> {
            if self.complete { Ok(b"test observation".to_vec()) } else { Err("pending".into()) }
        }
        async fn restore_request_facts(
            &self,
            operation: &str,
            target: &str,
            owner: &str,
        ) -> Result<(String, Vec<String>), String> {
            if (operation, target, owner) != ("restore-op", "target-db", "tenant/project") {
                return Err("restore identity mismatch".into());
            }
            Ok(("retained-request-digest".into(), vec!["gru".into(), "sfo".into(), "fra".into()]))
        }
        async fn restore_stats(
            &self,
            request: &RestoreStatsRequest,
        ) -> Result<NativeRestoreReceipt, String> {
            if !self.complete || request.journal_cut != "42" || request.restore_epoch != 7 {
                return Err("restore is not complete or identity mismatched".into());
            }
            Ok(NativeRestoreReceipt {
                selected_identity: "manifest-sha256".into(),
                logical_content_checksum: "logical-blake3".into(),
                restored_documents: 12,
                journal_cut: 42,
            })
        }
        async fn ensure_snapshot(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            unreachable!("stats must not mutate lifecycle")
        }
        async fn close_database(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            unreachable!("stats must not mutate lifecycle")
        }
        async fn stop_native_backup(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            unreachable!("stats must not mutate lifecycle")
        }
        async fn reactivate(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            unreachable!("stats must not mutate lifecycle")
        }
    }
    struct Authority;
    impl LifecycleAuthority for Authority {
        fn verify(
            &self,
            assignment: &AcceptedLifecycleAssignment,
            action: LifecycleGrantAction,
            operation: &str,
            signature: &[u8],
            grant: &[u8],
            expected_digest: Option<&str>,
        ) -> Result<(), String> {
            if assignment.tenant != "tenant"
                || assignment.project != "project"
                || operation != "restore-op"
                || action != LifecycleGrantAction::RestoreToTarget
                || signature != b"signature"
                || grant != b"grant"
                || expected_digest != Some("retained-request-digest")
            {
                return Err("authority mismatch".into());
            }
            Ok(())
        }
    }
    fn request() -> RestoreStatsRequest {
        RestoreStatsRequest {
            context: Some(LifecycleControlContext {
                tenant: "tenant".into(),
                project: "project".into(),
                region: "gru".into(),
                authority_signature: b"signature".to_vec(),
                operation_grant: b"grant".to_vec(),
                ..Default::default()
            }),
            operation_id: "restore-op".into(),
            target_owner: "tenant/project".into(),
            target_database: "target-db".into(),
            journal_cut: "42".into(),
            restore_epoch: 7,
            envelope: None,
        }
    }
    fn control(complete: bool) -> ProductionLifecycleControl {
        ProductionLifecycleControl {
            worker: Arc::new(Worker { complete }),
            authority: Arc::new(Authority),
        }
    }
    #[tokio::test]
    async fn restore_stats_preserves_verified_three_region_placement_and_identity() {
        let request = request();
        let stats = control(true).restore_stats(&request).await.expect("complete verified stats");
        assert_eq!(stats.placement, ["gru", "sfo", "fra"]);
        assert_eq!(stats.checksum, "logical-blake3");
        assert_eq!(stats.row_count, 12);
        assert_eq!(stats.journal_cut, "42");
        assert_eq!(stats.signed_completed_receipt, b"test observation");
        assert!(control(false).restore_stats(&request).await.is_err());
        let mut wrong_owner = request.clone();
        wrong_owner.target_owner = "other-tenant/project".into();
        assert!(control(true).restore_stats(&wrong_owner).await.is_err());
        let mut wrong_epoch = request.clone();
        wrong_epoch.restore_epoch = 8;
        assert!(control(true).restore_stats(&wrong_epoch).await.is_err());
        let mut wrong_authority = request;
        wrong_authority.context.as_mut().expect("context").operation_grant =
            b"wrong-grant".to_vec();
        assert!(control(true).restore_stats(&wrong_authority).await.is_err());
    }
}

#[cfg(test)]
mod delete_owned_data_tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use terrabase_tbep::proto::terrabase::v1::{LifecycleControlContext, LifecycleStatus};

    struct Worker {
        purges: AtomicUsize,
    }

    #[async_trait::async_trait]
    impl NativeLifecycleWorker for Worker {
        async fn ensure_snapshot(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            Err("unused".into())
        }

        async fn close_database(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            Err("unused".into())
        }

        async fn stop_native_backup(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            Err("unused".into())
        }

        async fn reactivate(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<NativeLifecycleReceipt, String> {
            Err("unused".into())
        }

        async fn delete_owned_data(
            &self,
            _: &AcceptedLifecycleAssignment,
            _: &str,
        ) -> Result<(), String> {
            self.purges.fetch_add(1, Ordering::Relaxed);
            Ok(())
        }
    }

    struct Authority;

    impl LifecycleAuthority for Authority {
        fn verify(
            &self,
            _: &AcceptedLifecycleAssignment,
            action: LifecycleGrantAction,
            operation: &str,
            _: &[u8],
            _: &[u8],
            _: Option<&str>,
        ) -> Result<(), String> {
            if action == LifecycleGrantAction::DeleteOwnedData && operation == "delete-op" {
                Ok(())
            } else {
                Err("unexpected lifecycle operation".into())
            }
        }
    }

    fn context() -> LifecycleControlContext {
        LifecycleControlContext {
            environment: "production".into(),
            tenant: "tenant".into(),
            project: "project".into(),
            database: "database".into(),
            region: "GRU".into(),
            member: "member".into(),
            placement_revision: 1,
            authority_epoch: 1,
            authority_lease: "lease".into(),
            authority_signature: b"signature".to_vec(),
            operation_grant: b"grant".to_vec(),
            ..Default::default()
        }
    }

    #[tokio::test]
    async fn provider_purge_success_is_applied_and_replay_remains_applied() {
        let worker = Arc::new(Worker { purges: AtomicUsize::new(0) });
        let control = TbepLifecycleControl::new(Arc::new(ProductionLifecycleControl {
            worker: Arc::clone(&worker) as Arc<dyn NativeLifecycleWorker>,
            authority: Arc::new(Authority),
        }));
        let context = context();

        let first = terrabase_tbep::lifecycle::LifecycleControl::delete_owned_data(
            &control,
            &context,
            "delete-op",
        )
        .await
        .expect("provider purge is applied");
        let replay = terrabase_tbep::lifecycle::LifecycleControl::delete_owned_data(
            &control,
            &context,
            "delete-op",
        )
        .await
        .expect("idempotent replay remains applied");

        assert_eq!(first.status, LifecycleStatus::Applied as i32);
        assert_eq!(replay, first);
        assert_eq!(worker.purges.load(Ordering::Relaxed), 2);
    }
}
