//! The HiLo identity-distribution contract (GAP-F09).
//!
//! Stage A — the naive first cut: blocks are allocated from the identity
//! family and minted locally, but the distribution discipline of the
//! decision record (`docs/families/hilo-identity-distribution.md`) is not
//! implemented yet. This stage exists to record the behavioral red
//! observations (member loss re-handing the dead owner's in-flight block,
//! ungated minting, no plan-level exhaustion refusal) before the burn and
//! acknowledgement discipline lands.

use crate::engine::Engine;
use crate::families::payloads::{validate_name, MAX_IDENTITY_VALUE};
use crate::families::{
    FamilyError, FamilyReadContext, FamilyReadRequest, FamilyReadResult, IdentityCommand,
    IdentityRead, IdentityWrite,
};
use crate::types::{Cancellation, Deadline, OperationId, TenantScope};

/// V1's minimum block capacity: `CalculateCapacity` never returns less
/// (`HiLoHandlerProcessorForGetNextHiLo.cs:71`, `:77`, `:82`, `:85`).
pub const MIN_BLOCK_SIZE: u64 = 32;

/// V1's maximum block capacity: the fast-window doubling is capped at
/// 1 Mi (`HiLoHandlerProcessorForGetNextHiLo.cs:77`).
pub const MAX_BLOCK_SIZE: u64 = 1024 * 1024;

/// The identity ceiling: the representable maximum of one identity name
/// (`src/families/payloads.rs:37`), re-expressed as the distribution
/// layer's own bound.
pub const IDENTITY_CEILING: u64 = MAX_IDENTITY_VALUE;

/// How a committed block's replication outcome is reported to the
/// distributor. The families seam owns no transport, so the host maps the
/// replication fact onto this vocabulary.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BlockDurability {
    /// Recorded only on the allocating member: it does not survive that
    /// member's loss.
    OwnerLocal,
    /// Durably recorded on members that survive the allocating member's
    /// loss.
    SurvivesOwnerLoss,
}

/// HiLo distribution failures.
///
/// Every case is typed and carries no document value, storage detail,
/// path, credential, or private implementation type.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HiLoError {
    /// The block size is outside V1's floor-to-ceiling bounds.
    BlockSizeInvalid,
    /// The identity name fails the identity family's name rules.
    NameInvalid,
    /// A new allocation was requested while one is outstanding.
    AllocationOutstanding,
    /// The live block still holds ids: V1 mints from the local range
    /// before fetching the next one.
    BlockNotExhausted,
    /// The block is not durably recorded beyond its allocator, so its ids
    /// must not be handed out.
    BlockNotDurable,
    /// A completion was reported while no allocation step was in that
    /// phase.
    InvalidPhase,
    /// The planned allocation would cross the identity ceiling.
    Exhausted,
    /// The family seam refused the read or the staged write.
    Family(FamilyError),
}

impl From<FamilyError> for HiLoError {
    fn from(error: FamilyError) -> Self {
        Self::Family(error)
    }
}

/// One allocated identity block: the half-open range `[low, high)`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HiLoBlock {
    low: u64,
    high: u64,
}

impl HiLoBlock {
    /// Creates one block range. Crate-only: blocks originate from the
    /// identity family's allocation, never from caller arithmetic.
    pub(crate) fn new(low: u64, high: u64) -> Self {
        Self { low, high }
    }

    /// Returns the first id of the block.
    #[must_use]
    pub fn low(&self) -> u64 {
        self.low
    }

    /// Returns one past the last id of the block.
    #[must_use]
    pub fn high(&self) -> u64 {
        self.high
    }

    /// Returns the number of ids the block spans.
    #[must_use]
    pub fn size(&self) -> u64 {
        self.high - self.low
    }

    /// Reports whether one id lies inside the block.
    #[must_use]
    pub fn contains(&self, id: u64) -> bool {
        id >= self.low && id < self.high
    }
}

/// The distribution phase of one distributor.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Phase {
    /// No allocation in flight.
    Idle,
    /// An allocation is planned but not committed.
    Planned { next: u64 },
    /// A block is committed but not acknowledged at a loss-surviving
    /// level.
    Pending(HiLoBlock),
    /// A block is acknowledged and handing out ids.
    Live { block: HiLoBlock, cursor: u64 },
}

/// One HiLo distributor: the V2 counterpart of V1's client-side
/// `AsyncHiLoIdGenerator`, holding one identity name's live block.
#[derive(Debug)]
pub struct HiLoDistributor {
    name: String,
    block_size: u64,
    phase: Phase,
    burned: u64,
}

impl HiLoDistributor {
    /// Creates one distributor for one identity name and block size.
    ///
    /// # Errors
    ///
    /// Returns [`HiLoError::BlockSizeInvalid`] for a size outside V1's
    /// floor-to-ceiling bounds and [`HiLoError::NameInvalid`] for a name
    /// the identity family refuses.
    pub fn try_new(name: String, block_size: u64) -> Result<Self, HiLoError> {
        validate_name(&name).map_err(|_| HiLoError::NameInvalid)?;
        if !(MIN_BLOCK_SIZE..=MAX_BLOCK_SIZE).contains(&block_size) {
            return Err(HiLoError::BlockSizeInvalid);
        }
        Ok(Self { name, block_size, phase: Phase::Idle, burned: 0 })
    }

    /// Returns the identity name this distributor distributes.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the configured block size.
    #[must_use]
    pub fn block_size(&self) -> u64 {
        self.block_size
    }

    /// Returns the total ids burned by transition burns.
    #[must_use]
    pub fn burned(&self) -> u64 {
        self.burned
    }

    /// Returns the acknowledged, hand-out-live block, when one exists.
    #[must_use]
    pub fn live_block(&self) -> Option<HiLoBlock> {
        match self.phase {
            Phase::Live { block, .. } => Some(block),
            _ => None,
        }
    }

    /// Returns the committed-but-unacknowledged block, when one exists.
    #[must_use]
    pub fn pending_block(&self) -> Option<HiLoBlock> {
        match self.phase {
            Phase::Pending(block) => Some(block),
            _ => None,
        }
    }

    /// Plans the distributor's next allocation: reads the identity's
    /// `next` through the Engine family seam and returns the identity
    /// write the caller commits through the owner's family-write path.
    ///
    /// # Errors
    ///
    /// Returns [`HiLoError::AllocationOutstanding`] while an allocation
    /// is planned or pending, [`HiLoError::BlockNotExhausted`] while the
    /// live block still holds ids, and the family seam's typed failures
    /// otherwise.
    pub fn plan_allocation(
        &mut self,
        engine: &Engine,
        scope: &TenantScope,
        operation: OperationId,
        deadline: Deadline,
    ) -> Result<IdentityWrite, HiLoError> {
        match self.phase {
            Phase::Idle | Phase::Live { .. } => {}
            Phase::Planned { .. } | Phase::Pending(_) => {
                return Err(HiLoError::AllocationOutstanding);
            }
        }
        let next = self.identity_next(engine, scope)?;
        let write = IdentityWrite::try_new(
            scope.clone(),
            self.name.clone(),
            IdentityCommand::Allocate { name: self.name.clone(), count: self.block_size },
            operation,
            deadline,
            Cancellation::new(),
        )
        .map_err(|_| HiLoError::NameInvalid)?;
        self.phase = Phase::Planned { next };
        Ok(write)
    }

    /// Records that the planned allocation committed. Stage A mints
    /// straight from the fetched range — V1's client shape — so the block
    /// is live immediately.
    ///
    /// # Errors
    ///
    /// Returns [`HiLoError::InvalidPhase`] when no allocation is planned.
    pub fn allocation_committed(&mut self) -> Result<(), HiLoError> {
        match self.phase {
            Phase::Planned { next } => {
                let block = HiLoBlock::new(next, next + self.block_size);
                self.phase = Phase::Live { block, cursor: next };
                Ok(())
            }
            _ => Err(HiLoError::InvalidPhase),
        }
    }

    /// Clears a planned allocation whose commit was refused or never ran.
    /// Nothing durable happened, so the distributor returns to idle.
    pub fn allocation_abandoned(&mut self) {
        if matches!(self.phase, Phase::Planned { .. }) {
            self.phase = Phase::Idle;
        }
    }

    /// Records the pending block's replication outcome.
    ///
    /// # Errors
    ///
    /// Returns [`HiLoError::BlockNotDurable`] when the reported durability
    /// does not survive the allocator's loss and
    /// [`HiLoError::InvalidPhase`] when no block is pending.
    pub fn acknowledged(&mut self, _durability: BlockDurability) -> Result<(), HiLoError> {
        Ok(())
    }

    /// Mints the next id from the live block.
    ///
    /// # Errors
    ///
    /// Returns [`HiLoError::BlockNotDurable`] while the block is pending;
    /// `Ok(None)` means no id is mintable right now.
    pub fn next_id(&mut self) -> Result<Option<u64>, HiLoError> {
        match self.phase {
            Phase::Live { block, ref mut cursor } => {
                if *cursor < block.high() {
                    let id = *cursor;
                    *cursor += 1;
                    Ok(Some(id))
                } else {
                    Ok(None)
                }
            }
            _ => Ok(None),
        }
    }

    /// Reads the identity name's `next` through the family seam.
    fn identity_next(&self, engine: &Engine, scope: &TenantScope) -> Result<u64, HiLoError> {
        let snapshot = engine.read_snapshot(scope).map_err(|error| match error {
            crate::engine::EngineError::SnapshotEngineMismatch
            | crate::engine::EngineError::ScopeMismatch => FamilyError::SnapshotEngineMismatch,
            crate::engine::EngineError::SnapshotTenantMismatch => FamilyError::TenantMismatch,
            _ => FamilyError::StorageFailure,
        })?;
        let read =
            IdentityRead::try_new(vec![self.name.clone()], Deadline::after(std::time::Duration::from_secs(45)).expect("validation deadline"), Cancellation::new())
                .map_err(|_| HiLoError::NameInvalid)?;
        let context = FamilyReadContext::try_new(scope.clone(), snapshot)
            .map_err(HiLoError::Family)?;
        let result = engine
            .families()
            .read(context, FamilyReadRequest::identity_range(read))
            .map_err(HiLoError::Family)?;
        let FamilyReadResult::IdentityRange(range) = result else {
            return Err(HiLoError::Family(FamilyError::InvalidRequest));
        };
        range
            .entries()
            .iter()
            .find(|entry| entry.name() == self.name)
            .map(|entry| entry.next())
            .ok_or(HiLoError::Family(FamilyError::InvalidRequest))
    }
}

#[cfg(test)]
mod tests;
