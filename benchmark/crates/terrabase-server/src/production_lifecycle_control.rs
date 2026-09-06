//! Production lifecycle adapter seam.
//!
//! This handoff requires accepted-placement proof verification before native
//! mutation. Request epoch and mTLS scope are never treated as authority.

use std::sync::Arc;

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
    ) -> Result<(), String>;
    /// Closes serving while retaining durable data.
    fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<(), String>;
    /// Stops only this database's native backup stream.
    fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<(), String>;
    /// Resumes the same retained database without replacing its identity.
    fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        operation: &str,
    ) -> Result<(), String>;
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
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.ensure_snapshot(assignment, operation)
    }

    /// Closes serving after verifying accepted authority.
    pub fn close_database(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.close_database(assignment, operation)
    }

    /// Stops one native backup stream after verifying accepted authority.
    pub fn stop_native_backup(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.stop_native_backup(assignment, operation)
    }

    /// Verifies accepted authority before resuming the same retained database.
    pub fn reactivate(
        &self,
        assignment: &AcceptedLifecycleAssignment,
        signature: &[u8],
        operation: &str,
    ) -> Result<(), String> {
        self.authority.verify(assignment, signature)?;
        self.worker.reactivate(assignment, operation)
    }
}
