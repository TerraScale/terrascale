//! Durable regional refill installation and same-transaction consumption.
use crate::{storage::CommitIntent, types::TenantId, usage::{BudgetConsumption, VerifiedAllocation}};
use rusqlite::{Connection, OptionalExtension, Transaction, params};

pub(super) fn install(tx: &Transaction<'_>, tenant: &TenantId, allocation: &VerifiedAllocation) -> Result<(), ()> {
    let g = allocation.grant();
    let prior: Option<Vec<u8>> = tx.query_row("SELECT payload_digest FROM usage_budget_allocations WHERE tenant=?1 AND operation=?2", params![tenant.as_str(), g.refill_operation_id], |r| r.get(0)).optional().map_err(|_| ())?;
    if let Some(prior) = prior { return if prior == allocation.payload_digest().as_slice() { Ok(()) } else { Err(()) }; }
    let sequence: i64 = g.pool_sequence.try_into().map_err(|_| ())?;
    let previous: i64 = tx.query_row("SELECT COALESCE(MAX(pool_sequence),0) FROM usage_budget_allocations WHERE tenant=?1 AND pool_id=?2", params![tenant.as_str(), g.pool_id], |r| r.get(0)).map_err(|_| ())?;
    if sequence <= previous { return Err(()); }
    tx.execute("INSERT INTO usage_budget_allocations
      (tenant,operation,database_id,branch_id,holder_id,cycle_id,policy_epoch,consent_epoch,writer_epoch,placement,payload_digest,units_total,units_remaining,expires_at,pool_id,pool_sequence)
      VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?12,?13,?14,?15)",
      params![tenant.as_str(), g.refill_operation_id, g.database_id, g.branch_id, g.holder_id, allocation.cycle_id(), g.policy_epoch as i64, g.consent_epoch as i64, g.writer_epoch as i64, allocation.placement(), allocation.payload_digest().as_slice(), allocation.local_units(), g.expires_at, g.pool_id, sequence]).map_err(|_| ())?;
    Ok(())
}

pub(super) fn remaining(connection: &Connection, tenant: &TenantId, refill: &str) -> Result<Option<i64>, ()> {
    connection.query_row("SELECT units_remaining FROM usage_budget_allocations WHERE tenant=?1 AND operation=?2", params![tenant.as_str(), refill], |r| r.get(0)).optional().map_err(|_| ())
}

pub(super) fn consumptions(connection: &Connection, tenant: &TenantId, after: u64, limit: u32) -> Result<Vec<BudgetConsumption>, ()> {
    let after = i64::try_from(after).map_err(|_| ())?;
    let mut query = connection.prepare("SELECT report_sequence,operation,pool_id,refill_operation_id,database_id,body_digest,units FROM usage_budget_operations WHERE tenant=?1 AND report_sequence>?2 ORDER BY report_sequence LIMIT ?3").map_err(|_| ())?;
    query.query_map(params![tenant.as_str(), after, limit], |r| Ok(BudgetConsumption { sequence: r.get(0)?, operation_id: r.get(1)?, pool_id: r.get(2)?, refill_operation_id: r.get(3)?, database_id: r.get(4)?, body_digest: r.get(5)?, local_units: r.get(6)? })).map_err(|_| ())?.collect::<Result<Vec<_>, _>>().map_err(|_| ())
}

pub(super) fn consume(tx: &Transaction<'_>, tenant: &TenantId, operation: &str, allocation: &VerifiedAllocation, units: i64, operation_digest: u64) -> Result<(), ()> {
    if units <= 0 || units > allocation.local_units() { return Err(()); }
    let g = allocation.grant();
    let body_digest = hex::encode(aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, &operation_digest.to_le_bytes()).as_ref());
    let prior: Option<(String, i64)> = tx.query_row("SELECT body_digest,units FROM usage_budget_operations WHERE tenant=?1 AND operation=?2 AND database_id=?3", params![tenant.as_str(), operation, g.database_id], |r| Ok((r.get(0)?, r.get(1)?))).optional().map_err(|_| ())?;
    if let Some((digest, prior_units)) = prior { return if digest == body_digest && prior_units == units { Ok(()) } else { Err(()) }; }
    install(tx, tenant, allocation)?;
    let changed = tx.execute("UPDATE usage_budget_allocations SET units_remaining=units_remaining-?1 WHERE tenant=?2 AND operation=?3 AND database_id=?4 AND payload_digest=?5 AND units_remaining>=?1",
      params![units, tenant.as_str(), g.refill_operation_id, g.database_id, allocation.payload_digest().as_slice()]).map_err(|_| ())?;
    if changed != 1 { return Err(()); }
    tx.execute("INSERT INTO usage_budget_operations(tenant,operation,database_id,pool_id,payload_digest,units,refill_operation_id,body_digest) VALUES(?1,?2,?3,?4,?5,?6,?7,?8)",
      params![tenant.as_str(), operation, g.database_id, g.pool_id, allocation.payload_digest().as_slice(), units, g.refill_operation_id, body_digest]).map_err(|_| ())?;
    Ok(())
}

pub(super) fn required(intent: &CommitIntent) -> bool {
    intent.budget_allocation.is_some() && !intent.internal_maintenance
}
