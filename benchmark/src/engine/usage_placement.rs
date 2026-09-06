//! Trusted placement binding for same-transaction automatic usage accounting.
use super::{Engine, TenantScope, UsageCommandError};
use crate::usage::UsagePlacement;

impl Engine {
    /// Persists the accepted placement before any customer write.
    /// Exact reopen is idempotent; changing its scope or revision is refused.
    ///
    /// # Errors
    /// Returns scope mismatch, invalid placement, or durable storage errors.
    #[allow(clippy::needless_pass_by_value)]
    pub fn configure_usage_placement(
        &self,
        placement: UsagePlacement,
    ) -> Result<(), UsageCommandError> {
        self.validate_tenant(placement.scope.tenant_id())
            .map_err(|_| UsageCommandError::ScopeMismatch)?;
        if self.owner_scope_enforced
            && (!self.usage_owner_matches(
                placement.scope.project_id().as_str(),
                placement.scope.database_id().as_str(),
            ) || *placement.scope.region_id() != self.durable_owner.region())
        {
            return Err(UsageCommandError::ScopeMismatch);
        }
        let validated = UsagePlacement::try_new(
            placement.scope.tenant_id().clone(),
            placement.scope.project_id().as_str(),
            placement.scope.database_id().as_str(),
            placement.scope.partition_id().as_str(),
            *placement.scope.region_id(),
            placement.selected_regions.clone(),
            &placement.revision,
        )
        .map_err(|_| UsageCommandError::InvalidRequest)?;
        self.lock_storage()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .configure_usage_placement(&validated)
            .map_err(|()| UsageCommandError::UsageUnavailable)
    }

    /// Returns the durable accepted placement that automatic metering uses.
    ///
    /// # Errors
    /// Returns scope or durable storage errors; absent binding remains explicit.
    pub fn usage_placement(
        &self,
        scope: &TenantScope,
    ) -> Result<Option<UsagePlacement>, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        self.lock_storage()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .usage_placement(scope.tenant())
            .map_err(|()| UsageCommandError::StorageUnavailable)
    }
}
