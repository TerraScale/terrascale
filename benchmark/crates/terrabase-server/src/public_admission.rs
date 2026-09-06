//! Shared owner resolution for the public API admission boundary.
//!
//! A database route is an accepted identity only after it has been resolved
//! through the retained production registry.  Credential verification uses
//! this result as its owner binding; claims and deployment defaults never
//! select the database owner.

use crate::api::{ApiError, ApiState};

/// Resolves the immutable tenant/project attached to an accepted route.
pub(crate) fn route_owner(state: &ApiState, database: &str) -> Result<(String, String), ApiError> {
    if let Some(registry) = &state.admitted_databases {
        let admitted = registry
            .resolve(database)
            .map_err(|_| ApiError::storage_failure())?
            .ok_or_else(|| ApiError::database_not_found(database))?;
        return Ok((admitted.record.tenant_id.clone(), admitted.record.project_id.clone()));
    }
    Ok(state.scope.clone())
}
