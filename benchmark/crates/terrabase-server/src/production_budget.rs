//! Production binding from delivered allocations to origin transactions.
//!
//! The allocation journal is the control-plane handoff. This module is the
//! small serving-side adapter that turns that handoff into the two trusted
//! pieces the Engine requires on a billable origin commit.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::Mutex;

use terrabase::regions::RegionCode;
use terrabase::usage::UsageAdmissionContext;
use terrabase::{Transaction, UsageFactId};
use terrabase_cluster::wal_route::DurableWalAssignmentSource;

use crate::usage_admission::UsageAdmissionJournal;

/// Resolves allocation bindings from the records accepted by the real
/// admission host and the retained signed placement journal.
pub struct AcceptedAdmissionBindingResolver {
    records: Arc<Mutex<BTreeMap<String, crate::admission::AdmissionRecord>>>,
    journal: Arc<terrabase_tbep::placement::PlacementJournal>,
}

impl AcceptedAdmissionBindingResolver {
    /// Creates a budget view over admission records and placement authority.
    #[must_use]
    pub fn new(
        records: Arc<Mutex<BTreeMap<String, crate::admission::AdmissionRecord>>>,
        journal: Arc<terrabase_tbep::placement::PlacementJournal>,
    ) -> Self {
        Self { records, journal }
    }
}

impl crate::usage_admission::AllocationBindingResolver for AcceptedAdmissionBindingResolver {
    fn binding(&self, database_id: &str) -> Result<(String, String, String, u64), String> {
        let record = self
            .records
            .lock()
            .map_err(|_| "admission registry poisoned")?
            .get(database_id)
            .cloned()
            .ok_or("database admission is unavailable")?;
        let accepted = self.journal.accepted().map_err(|_| "accepted placement unavailable")?;
        let scope = terrabase_cluster::production::DatabaseRouteScope::try_new(
            &record.tenant_id,
            &record.project_id,
            database_id,
        )
        .map_err(|_| "database scope is invalid")?;
        let assignment = self
            .journal
            .load_assignment(scope)
            .map_err(|_| "accepted database assignment unavailable")?;
        if assignment.epoch == 0 || assignment.writer.member().as_str() != record.member_id {
            return Err("database is not admitted at the local writer epoch".into());
        }
        Ok((record.tenant_id, record.member_id, accepted.proof_digest, assignment.epoch))
    }
}

/// Runtime owner for production write-budget consumption.
#[derive(Clone)]
pub struct ProductionBudgetRuntime {
    journal: Arc<UsageAdmissionJournal>,
}

impl ProductionBudgetRuntime {
    /// Binds the runtime to the already-composed admission journal.
    #[must_use]
    pub fn new(journal: Arc<UsageAdmissionJournal>) -> Self {
        Self { journal }
    }

    /// Attaches the persisted, currently valid allocation and its matching
    /// usage fact context to an origin transaction. The operation and scope
    /// come from the authenticated request; holder, placement, epochs and
    /// grant identity come only from the verified persisted allocation.
    pub fn attach(
        &self,
        transaction: &mut Transaction<'_>,
        tenant_id: &str,
        project_id: &str,
        database_id: &str,
        operation_id: &str,
        origin_region: RegionCode,
        units: u64,
    ) -> Result<(), String> {
        let allocation = self
            .journal
            .resolve_allocation(operation_id, database_id)?
            .ok_or("write allocation unavailable")?;
        if allocation.grant().database_id != database_id
            || allocation.grant().organization_id != tenant_id
            || allocation.grant().operation_id != operation_id
            || units == 0
            || units
                > u64::try_from(allocation.local_units()).map_err(|_| "allocation units invalid")?
        {
            return Err("write allocation does not cover this operation".into());
        }
        let request_id = UsageFactId::try_new(operation_id)
            .map_err(|_| "write operation identity is invalid")?;
        let context = UsageAdmissionContext::try_new(
            project_id,
            database_id,
            &allocation.grant().branch_id,
            origin_region,
            request_id,
            unix_millis(),
            1,
            i128::from(units),
        )
        .map_err(|_| "write usage context is invalid")?;
        transaction
            .attach_usage_admission(context)
            .map_err(|_| "write usage context could not be attached")?;
        transaction
            .attach_usage_budget(allocation)
            .map_err(|_| "write allocation could not be attached")?;
        Ok(())
    }
}

fn unix_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |duration| duration.as_millis().try_into().unwrap_or(u64::MAX))
}
