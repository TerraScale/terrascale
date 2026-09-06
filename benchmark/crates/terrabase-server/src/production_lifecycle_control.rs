//! Production lifecycle adapter seam.
//!
//! This handoff requires accepted-placement proof verification before native
//! mutation. Request epoch and mTLS scope are never treated as authority.

use std::sync::Arc;

/// Receipt of a completed native lifecycle effect.
pub enum NativeLifecycleReceipt {
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
        signature: &[u8],
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
            .map(|_| ())
            .map_err(|_| "accepted lifecycle authority proof is invalid or stale".into())
    }
}

impl TbepLifecycleControl {
    #[must_use]
    pub fn new(control: Arc<ProductionLifecycleControl>) -> Self {
        Self { control }
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

    fn invoke(
        &self,
        context: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        operation: &str,
        f: impl FnOnce(
            &ProductionLifecycleControl,
            &AcceptedLifecycleAssignment,
            &[u8],
            &str,
        ) -> Result<NativeLifecycleReceipt, String>,
    ) -> Result<(), String> {
        let assignment = Self::assignment(context);
        f(&self.control, &assignment, &context.authority_signature, operation).map(|_| ())
    }
}

impl terrabase_tbep::lifecycle::LifecycleControl for TbepLifecycleControl {
    fn ensure_snapshot(
        &self,
        c: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        o: &str,
    ) -> Result<(), String> {
        self.invoke(c, o, ProductionLifecycleControl::ensure_snapshot)
    }
    fn verify_snapshot_placement(
        &self,
        c: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        o: &str,
    ) -> Result<(), String> {
        self.invoke(c, o, ProductionLifecycleControl::verify_snapshot_placement)
    }
    fn close_database(
        &self,
        c: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        o: &str,
    ) -> Result<(), String> {
        self.invoke(c, o, ProductionLifecycleControl::close_database)
    }
    fn stop_native_backup(
        &self,
        c: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        o: &str,
    ) -> Result<(), String> {
        self.invoke(c, o, ProductionLifecycleControl::stop_native_backup)
    }
    fn reactivate(
        &self,
        c: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        o: &str,
    ) -> Result<(), String> {
        self.invoke(c, o, ProductionLifecycleControl::reactivate)
    }
    fn delete_owned_data(
        &self,
        c: &terrabase_tbep::proto::terrabase::v1::LifecycleControlContext,
        o: &str,
    ) -> Result<(), String> {
        self.invoke(c, o, ProductionLifecycleControl::delete_owned_data)
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
pub trait NativeLifecycleWorker: Send + Sync {
    /// Ensures all selected destinations contain a complete snapshot receipt.
    fn ensure_snapshot(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Closes serving while retaining durable data.
    fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Stops only this database's native backup stream.
    fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Resumes the same retained database without replacing its identity.
    fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String>;
    /// Verifies the native placement without changing serving state.
    fn verify_snapshot_placement(
        &self,
        _assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<(), String> {
        Err("native snapshot placement verifier is unavailable".into())
    }
    /// Deletes data owned by this member after durable authority admission.
    fn delete_owned_data(
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
}

impl NativeLifecycleWorker for RegistryLifecycleWorker {
    fn ensure_snapshot(
        &self,
        _assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<NativeLifecycleReceipt, String> {
        Err("native snapshot fleet is not attached".into())
    }

    fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
        self.registry.pause(&assignment.database, _operation).map(NativeLifecycleReceipt::State)
    }

    fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
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
                    retained_points: 0,
                })
            })
            .map_err(|_| "native backup pause refused".into())
    }

    fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        _operation: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
        self.registry.resume(&assignment.database, _operation).map(NativeLifecycleReceipt::State)
    }
}

/// Authority verifier for signed accepted placement proofs.
pub trait LifecycleAuthority: Send + Sync {
    /// Verifies proof against currently retained accepted state.
    fn verify(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
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
    pub fn ensure_snapshot(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<NativeLifecycleReceipt, String> {
        self.authority.verify(assignment, signature)?;
        self.worker.ensure_snapshot(assignment, operation).map(|_| ())
    }

    /// Closes serving after verifying accepted authority.
    pub fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.close_database(assignment, operation).map(|_| ())
    }

    /// Stops one native backup stream after verifying accepted authority.
    pub fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.stop_native_backup(assignment, operation).map(|_| ())
    }

    /// Verifies accepted authority before resuming the same retained database.
    pub fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.reactivate(assignment, operation).map(|_| ())
    }

    pub fn verify_snapshot_placement(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.verify_snapshot_placement(assignment, operation).map(|_| ())
    }

    pub fn delete_owned_data(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.delete_owned_data(assignment, operation)
    }
}
