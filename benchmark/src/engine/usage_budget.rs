//! Durable prefunded credit installation and asynchronous reporting.
use super::{Engine, TenantScope, UsageCommandError};
use crate::usage::{BudgetConsumption, VerifiedAllocation};

impl Engine {
    /// Installs a verified refill exactly once in the database credit journal.
    /// # Errors
    /// Rejects foreign scope, conflicting or stale refills, and storage failures.
    pub fn install_usage_budget(
        &self,
        scope: &TenantScope,
        allocation: &VerifiedAllocation,
    ) -> Result<(), UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        if allocation.grant().organization_id != scope.tenant().as_str() {
            return Err(UsageCommandError::ScopeMismatch);
        }
        self.lock_storage()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .install_usage_budget(scope.tenant(), allocation)
            .map_err(|()| UsageCommandError::StorageUnavailable)
    }

    /// Reads the remaining local credits in one verified refill (absent means uninstalled).
    /// # Errors
    /// Rejects foreign scope or unavailable storage.
    pub fn usage_budget_remaining(
        &self,
        scope: &TenantScope,
        refill: &str,
    ) -> Result<Option<i64>, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        self.lock_storage()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .usage_budget_remaining(scope.tenant(), refill)
            .map_err(|()| UsageCommandError::StorageUnavailable)
    }

    /// Reads a bounded page of committed local debits for asynchronous GRU reconciliation.
    /// # Errors
    /// Rejects invalid limits, scope, or unavailable storage.
    pub fn usage_budget_consumptions(
        &self,
        scope: &TenantScope,
        after: u64,
        limit: u32,
    ) -> Result<Vec<BudgetConsumption>, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        if limit == 0 || limit > 64 {
            return Err(UsageCommandError::InvalidRequest);
        }
        self.lock_storage()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .usage_budget_consumptions(scope.tenant(), after, limit)
            .map_err(|()| UsageCommandError::StorageUnavailable)
    }
}
