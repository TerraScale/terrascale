//! Trusted, persisted database placement used by automatic usage minting.

use super::{UsageError, UsageScope, UsageScopeId};
use crate::regions::RegionCode;
use crate::types::TenantId;

/// Accepted placement metadata; callers never supply billable amounts.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsagePlacement {
    /// Owner and local origin stream identity.
    pub scope: UsageScope,
    /// Exact selected Regions, sorted and unique. Copies are charged once here.
    pub selected_regions: Vec<RegionCode>,
    /// Immutable accepted placement revision.
    pub revision: String,
}

impl UsagePlacement {
    /// Validates one trusted placement binding.
    ///
    /// # Errors
    /// Returns invalid scope, Region set, or revision errors.
    #[allow(clippy::too_many_arguments, reason = "the complete accepted placement tuple")]
    pub fn try_new(
        tenant: TenantId,
        project: &str,
        database: &str,
        partition: &str,
        origin: RegionCode,
        mut selected_regions: Vec<RegionCode>,
        revision: &str,
    ) -> Result<Self, UsageError> {
        let scope = UsageScope::try_new(tenant, project, database, partition, origin)?;
        UsageScopeId::try_new(revision)?;
        selected_regions.sort_unstable();
        if selected_regions.is_empty()
            || !selected_regions.contains(&origin)
            || selected_regions.windows(2).any(|pair| pair[0] == pair[1])
        {
            return Err(UsageError::InvalidRegionCount);
        }
        for region in &selected_regions {
            super::validate_usage_region(*region)?;
        }
        Ok(Self { scope, selected_regions, revision: revision.to_owned() })
    }
}
