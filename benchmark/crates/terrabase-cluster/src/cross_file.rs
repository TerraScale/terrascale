//! Cluster-owned qualification of DB-local cross-file receipts.

use crate::{production::DatabaseRouteScope, wal_driver::{WalPhysicalBoundary, WalWriterDriver}};
use terrabase::types::TenantScope;
use terrabase::engine::{CrossFileReceiptPhase, LocalCrossFileReceipt};

/// A local receipt qualified against an authenticated writer physical bound.
/// This type does not claim Global durability.
#[derive(Clone, Debug)]
pub struct Rf2DecisionQualification {
    local: LocalCrossFileReceipt,
    physical: WalPhysicalBoundary,
}

impl Rf2DecisionQualification {
    /// Local Engine receipt.
    #[must_use]
    pub fn local(&self) -> &LocalCrossFileReceipt { &self.local }
    /// Authenticated writer physical boundary.
    #[must_use]
    pub fn physical(&self) -> &WalPhysicalBoundary { &self.physical }
}

/// Validates that a local decision is covered by an admitted writer boundary.
fn qualify_rf2(
    local: LocalCrossFileReceipt,
    physical: WalPhysicalBoundary,
    scope: DatabaseRouteScope,
    epoch: u64,
    generation: u64,
) -> Result<Rf2DecisionQualification, &'static str> {
    if physical.scope() != scope || physical.epoch() != epoch || physical.generation() != generation {
        return Err("writer boundary scope or epoch mismatch");
    }
    if local.phase() != CrossFileReceiptPhase::Decision || local.durability() != terrabase::types::LocalDurability::Synced {
        return Err("receipt is not a synced decision");
    }
    if local.sequence() > physical.sequence() {
        return Err("writer boundary does not cover local decision");
    }
    Ok(Rf2DecisionQualification { local, physical })
}

/// Obtains the physical bound directly from the admitted writer driver before
/// qualifying a local Engine receipt. Callers cannot substitute follower
/// boundary bytes or sequence claims.
pub async fn qualify_rf2_from_writer(
    writer: &mut WalWriterDriver,
    tenant: TenantScope,
    local: LocalCrossFileReceipt,
    scope: DatabaseRouteScope,
    epoch: u64,
    generation: u64,
) -> Result<Option<Rf2DecisionQualification>, &'static str> {
    let Some(physical) = writer.physical_boundary(tenant).await.map_err(|_| "writer boundary unavailable")? else {
        return Ok(None);
    };
    qualify_rf2(local, physical, scope, epoch, generation).map(Some)
}
