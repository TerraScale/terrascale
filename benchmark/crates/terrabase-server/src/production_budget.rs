//! Production binding from delivered allocations to origin transactions.
//!
//! The allocation journal is the control-plane handoff. This module is the
//! small serving-side adapter that turns that handoff into the two trusted
//! pieces the Engine requires on a billable origin commit.

use std::sync::Arc;

use terrabase::{Transaction, UsageAdmissionContext, UsageFactId};
use terrabase::regions::RegionCode;

use crate::usage_admission::UsageAdmissionJournal;

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
            || allocation.grant().operation_id != operation_id
            || units == 0
            || units > u64::try_from(allocation.local_units()).map_err(|_| "allocation units invalid")?
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

