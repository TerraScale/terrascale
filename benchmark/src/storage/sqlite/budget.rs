//! Durable regional allocation consumption.

use crate::{storage::CommitIntent, types::TenantId, usage::VerifiedAllocation};
use rusqlite::{Transaction, params};

/// Consumes one regional allocation inside the caller's open commit
/// transaction. The grant row is installed on first delivery; all immutable
/// fields are checked on every retry and the balance update is conditional.
pub(super) fn consume(
    transaction: &Transaction<'_>,
    tenant: &TenantId,
    allocation: &VerifiedAllocation,
    units: i64,
) -> Result<(), ()> {
    if units <= 0 || units > allocation.local_units() {
        return Err(());
    }
    let grant = allocation.grant();
    transaction.execute(
        "INSERT OR IGNORE INTO usage_budget_allocations
         (tenant,operation,database_id,branch_id,holder_id,cycle_id,policy_epoch,consent_epoch,writer_epoch,placement,payload_digest,units_total,units_remaining,expires_at)
         VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?12,?13)",
        params![tenant.as_str(), grant.pool_id, grant.database_id, grant.branch_id, grant.holder_id,
            allocation.cycle_id(), grant.policy_epoch as i64, grant.consent_epoch as i64, grant.writer_epoch as i64, allocation.placement(), allocation.payload_digest().as_slice(), allocation.local_units(), grant.expires_at],
    ).map_err(|_| ())?;
    let changed = transaction
        .execute(
            "UPDATE usage_budget_allocations SET units_remaining=units_remaining-?1
         WHERE tenant=?2 AND operation=?3 AND database_id=?4 AND branch_id=?5 AND cycle_id=?6
           AND policy_epoch=?7 AND consent_epoch=?8 AND writer_epoch=?9 AND placement=?10 AND payload_digest=?11
           AND expires_at=?12 AND units_remaining>=?1",
            params![
                units,
                tenant.as_str(),
                grant.pool_id,
                grant.database_id,
                grant.branch_id,
                allocation.cycle_id(),
                grant.policy_epoch as i64,
                grant.consent_epoch as i64,
                grant.writer_epoch as i64,
                allocation.placement(),
                allocation.payload_digest().as_slice(),
                grant.expires_at
            ],
        )
        .map_err(|_| ())?;
    if changed != 1 {
        return Err(());
    }
    Ok(())
}

/// Ensures the grant's operation is attached to the same tenant operation.
pub(super) fn required(intent: &CommitIntent) -> bool {
    intent.budget_allocation.is_some() && !intent.internal_maintenance
}
