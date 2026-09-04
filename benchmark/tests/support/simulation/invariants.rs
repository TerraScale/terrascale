//! Assertions that are meaningful for the reference world.

use super::model::{JournalRow, ReferenceWorld, Region};

/// A reference-model invariant failure.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ModelInvariantError {
    /// An acknowledged row disappeared from durable origin state.
    MissingAcknowledgedRow,
    /// Region state differs after the modeled drain.
    DivergedRegions,
    /// A row was applied before one of its dependencies.
    CausalDependencyMissing,
}

/// Checks the model-only portions of ADR 0044 §4.
pub fn assert_model_invariants(
    world: &ReferenceWorld,
    regions: &[Region],
    acknowledged: &[JournalRow],
) -> Result<(), ModelInvariantError> {
    if acknowledged.iter().any(|row| !world.durable_rows().any(|durable| durable.dot == row.dot)) {
        return Err(ModelInvariantError::MissingAcknowledgedRow);
    }
    if regions.iter().any(|region| !world.has_all_rows(*region)) {
        return Err(ModelInvariantError::MissingAcknowledgedRow);
    }
    if regions.is_empty() {
        return Ok(());
    }
    let first_digest = world.digest(regions[0]);
    if regions.iter().skip(1).any(|region| world.digest(*region) != first_digest) {
        return Err(ModelInvariantError::DivergedRegions);
    }
    for row in acknowledged {
        for region in regions {
            if row.context.iter().any(|dependency| !world.has_applied(*region, *dependency)) {
                return Err(ModelInvariantError::CausalDependencyMissing);
            }
        }
    }
    Ok(())
}
