use super::{Engine, TenantScope, UsageCommandError, map_usage_storage_error};
use crate::usage::{MeasurementWindow, StorageUsageClosure};

impl Engine {
    /// Returns the last frozen storage boundary, absent before the first close.
    ///
    /// # Errors
    /// Returns a scope or durable storage error.
    pub fn storage_usage_closed_through(
        &self,
        scope: &TenantScope,
    ) -> Result<Option<u64>, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        self.storage
            .lock()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .storage_usage_closed_through(scope.tenant())
            .map_err(map_usage_storage_error)
    }
    /// Returns the durable accepted-deletion fence used to revoke public access.
    ///
    /// # Errors
    /// Returns a scope or storage integrity/availability error.
    pub fn storage_usage_stopped_at(
        &self,
        scope: &TenantScope,
    ) -> Result<Option<u64>, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        self.storage
            .lock()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .storage_usage_stopped_at(scope.tenant())
            .map_err(map_usage_storage_error)
    }
    /// Freezes a completed interval using the persisted placement binding and
    /// actual logical storage timeline. No caller supplies usage quantities.
    ///
    /// This trusted control-plane command requires the caller to establish
    /// replication completeness through the requested billing boundary first.
    /// An exact retry returns the original cut, including zero-use intervals.
    ///
    /// # Errors
    /// Returns scope, missing admission, overlapping closure, or storage errors.
    pub fn close_storage_usage(
        &self,
        scope: &TenantScope,
        window: MeasurementWindow,
    ) -> Result<StorageUsageClosure, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        self.storage
            .lock()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .close_storage_usage(scope.tenant(), window)
            .map_err(map_usage_storage_error)
    }

    /// Durably stops storage charging when database deletion is accepted.
    /// Cleanup and backup retention cannot resume customer accrual afterward.
    /// The supplied boundary is trusted lifecycle authority time.
    ///
    /// # Errors
    /// Returns a scope, conflicting deletion boundary, or storage failure.
    pub fn stop_storage_usage(
        &self,
        scope: &TenantScope,
        accepted_at_millis: u64,
    ) -> Result<(), UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        self.storage
            .lock()
            .map_err(|_| UsageCommandError::StorageUnavailable)?
            .port
            .stop_storage_usage(scope.tenant(), accepted_at_millis)
            .map_err(map_usage_storage_error)
    }
}
