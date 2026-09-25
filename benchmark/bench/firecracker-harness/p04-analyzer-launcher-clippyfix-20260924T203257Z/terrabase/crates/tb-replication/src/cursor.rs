//! Per-origin contiguous frontiers. Receipt never advances durable progress.

use std::collections::BTreeMap;
use std::error::Error;
use std::fmt;

use tb_core::DatabaseId;

use crate::event::{
    CommitEvent, ConfigurationId, EventDigest, EventId, MembershipEpoch, OriginId, ShardId,
};

/// Hard v1 bound on transient out-of-order identities in one origin stream.
pub const MAX_CURSOR_PENDING: usize = 1_024;
/// Hard v1 sequence window above the last durable prefix.
pub const MAX_CURSOR_SEQUENCE_DISTANCE: u64 = 1_024;

/// Limits for received/synced events held above the durable prefix.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CursorLimits {
    /// Maximum event identities tracked above the durable prefix.
    pub max_pending: usize,
    /// Greatest admitted distance from the durable prefix.
    pub max_sequence_distance: u64,
}

/// One certified writer stream to which a cursor is bound.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CursorScope {
    /// Database containing the stream.
    pub database: DatabaseId,
    /// Shard containing the stream.
    pub shard: ShardId,
    /// Writer and restart incarnation.
    pub origin: OriginId,
    /// Certified membership generation.
    pub epoch: MembershipEpoch,
    /// Certified configuration identity for that generation.
    pub configuration: ConfigurationId,
}

impl CursorLimits {
    pub(crate) fn validate(self) -> Result<Self, CursorError> {
        if self.max_pending == 0
            || self.max_pending > MAX_CURSOR_PENDING
            || self.max_sequence_distance == 0
            || self.max_sequence_distance > MAX_CURSOR_SEQUENCE_DISTANCE
        {
            Err(CursorError::InvalidLimits)
        } else {
            Ok(self)
        }
    }
}

/// Result of a bounded receive attempt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OfferOutcome {
    /// A new complete event entered the bounded pending set.
    Accepted,
    /// The same digest was already pending; no state changed.
    DuplicatePending,
    /// The ID is at or below the durable prefix. The caller must check its
    /// persisted identity/digest before treating it as a duplicate.
    RequiresStoredIdentityCheck,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Pending {
    digest: EventDigest,
    synced: bool,
}

/// A cursor for exactly one database, shard, writer incarnation, and active
/// membership epoch. The gap set is transient and bounded. Durable/applied
/// prefixes must be checkpointed by the integrating storage engine.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OriginCursor {
    database: DatabaseId,
    shard: ShardId,
    origin: OriginId,
    epoch: MembershipEpoch,
    configuration: ConfigurationId,
    durable: u64,
    applied: u64,
    pending: BTreeMap<u64, Pending>,
    limits: CursorLimits,
}

impl OriginCursor {
    /// Opens a fresh cursor before the first event of this incarnation.
    ///
    /// # Errors
    ///
    /// Returns an error for invalid identity or zero limits.
    pub fn new(scope: CursorScope, limits: CursorLimits) -> Result<Self, CursorError> {
        let limits = limits.validate()?;
        if scope.epoch.get() == 0 || scope.origin.incarnation == [0; 16] {
            return Err(CursorError::InvalidIdentity);
        }
        Ok(Self {
            database: scope.database,
            shard: scope.shard,
            origin: scope.origin,
            epoch: scope.epoch,
            configuration: scope.configuration,
            durable: 0,
            applied: 0,
            pending: BTreeMap::new(),
            limits,
        })
    }

    /// Pure restart transition. Only a future journal recovery path may call
    /// this after verifying a persisted checkpoint and replaying synced gaps.
    #[allow(dead_code, reason = "awaits durable replica journal integration")]
    pub(crate) fn from_recovered(
        scope: CursorScope,
        durable_prefix: u64,
        applied_prefix: u64,
        limits: CursorLimits,
    ) -> Result<Self, CursorError> {
        let mut cursor = Self::new(scope, limits)?;
        if applied_prefix > durable_prefix {
            return Err(CursorError::InvalidCheckpoint);
        }
        cursor.durable = durable_prefix;
        cursor.applied = applied_prefix;
        Ok(cursor)
    }

    /// Highest contiguous complete event received; this may vanish on restart.
    #[must_use]
    pub fn received_prefix(&self) -> u64 {
        let mut prefix = self.durable;
        while let Some(next) = prefix.checked_add(1) {
            if !self.pending.contains_key(&next) {
                break;
            }
            prefix = next;
        }
        prefix
    }

    /// Highest contiguous WAL-synced event.
    #[must_use]
    pub const fn durable_prefix(&self) -> u64 {
        self.durable
    }

    /// Highest contiguous atomically applied event.
    #[must_use]
    pub const fn applied_prefix(&self) -> u64 {
        self.applied
    }

    /// Number of received events still above the durable prefix.
    #[must_use]
    pub fn pending_len(&self) -> usize {
        self.pending.len()
    }

    /// Checks epoch, scope, identity collisions, and bounded gap admission.
    /// This never marks an event durable.
    ///
    /// # Errors
    ///
    /// Returns an error for wrong scope/epoch, changed digest, or gap limits.
    pub fn offer(&mut self, event: &CommitEvent) -> Result<OfferOutcome, CursorError> {
        self.offer_identity(
            event.id(),
            event.epoch(),
            event.configuration(),
            event.digest(),
        )
    }

    /// Admit an identity whose complete event bytes were verified by the
    /// private journal/codec boundary. This is not a durability transition.
    pub(crate) fn offer_identity(
        &mut self,
        id: EventId,
        epoch: MembershipEpoch,
        configuration: ConfigurationId,
        digest: EventDigest,
    ) -> Result<OfferOutcome, CursorError> {
        self.check_scope(id, epoch, configuration)?;
        let sequence = id.sequence();
        if sequence <= self.durable {
            return Ok(OfferOutcome::RequiresStoredIdentityCheck);
        }
        if sequence - self.durable > self.limits.max_sequence_distance {
            return Err(CursorError::GapBeyondWindow);
        }
        if let Some(pending) = self.pending.get(&sequence) {
            if pending.digest != digest {
                return Err(CursorError::ConflictingIdentity);
            }
            return Ok(OfferOutcome::DuplicatePending);
        }
        if self.pending.len() >= self.limits.max_pending {
            return Err(CursorError::PendingFull);
        }
        self.pending.insert(
            sequence,
            Pending {
                digest,
                synced: false,
            },
        );
        Ok(OfferOutcome::Accepted)
    }

    /// Pure transition called only by a future journal after WAL sync. An
    /// out-of-order synced event remains in the bounded pending set.
    #[allow(dead_code, reason = "awaits durable replica journal integration")]
    pub(crate) fn record_synced(
        &mut self,
        id: EventId,
        epoch: MembershipEpoch,
        configuration: ConfigurationId,
        digest: EventDigest,
    ) -> Result<u64, CursorError> {
        self.check_scope(id, epoch, configuration)?;
        if id.sequence() <= self.durable {
            return Ok(self.durable);
        }
        let pending = self
            .pending
            .get_mut(&id.sequence())
            .ok_or(CursorError::EventNotReceived)?;
        if pending.digest != digest {
            return Err(CursorError::ConflictingIdentity);
        }
        pending.synced = true;
        while let Some(next) = self.durable.checked_add(1) {
            if !self.pending.get(&next).is_some_and(|entry| entry.synced) {
                break;
            }
            self.pending.remove(&next);
            self.durable = next;
        }
        Ok(self.durable)
    }

    /// Pure transition called only by a future engine integration after a
    /// whole event was atomically applied in origin order.
    #[allow(dead_code, reason = "awaits durable replica journal integration")]
    pub(crate) fn record_applied(
        &mut self,
        id: EventId,
        epoch: MembershipEpoch,
        configuration: ConfigurationId,
    ) -> Result<u64, CursorError> {
        self.check_scope(id, epoch, configuration)?;
        if id.sequence() <= self.applied {
            return Ok(self.applied);
        }
        if id.sequence() > self.durable {
            return Err(CursorError::EventNotDurable);
        }
        if self.applied.checked_add(1) != Some(id.sequence()) {
            return Err(CursorError::ApplyGap);
        }
        self.applied = id.sequence();
        Ok(self.applied)
    }

    fn check_scope(
        &self,
        id: EventId,
        epoch: MembershipEpoch,
        configuration: ConfigurationId,
    ) -> Result<(), CursorError> {
        if id.database() != self.database || id.shard() != self.shard || id.origin() != self.origin
        {
            return Err(CursorError::WrongScope);
        }
        if epoch != self.epoch {
            return Err(CursorError::EpochMismatch);
        }
        if configuration != self.configuration {
            return Err(CursorError::ConfigurationMismatch);
        }
        Ok(())
    }
}

/// Admission or frontier validation failure. No failing operation changes a
/// frontier; a retained pending entry may already have been marked synced.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CursorError {
    /// Zero limits are not useful.
    InvalidLimits,
    /// An active cursor cannot have epoch zero or a zero incarnation.
    InvalidIdentity,
    /// Recovered applied progress exceeded durable progress.
    InvalidCheckpoint,
    /// Event targets another database, shard, or writer incarnation.
    WrongScope,
    /// Event epoch differs from the active certified epoch.
    EpochMismatch,
    /// The same epoch number names another certified configuration.
    ConfigurationMismatch,
    /// Event lies beyond the configured bounded sequence window.
    GapBeyondWindow,
    /// Too many received events wait above the durable prefix.
    PendingFull,
    /// One identity has two different canonical digests.
    ConflictingIdentity,
    /// Storage reported a committed event not first offered into this cursor.
    EventNotReceived,
    /// Apply proof arrived before the required storage barrier.
    EventNotDurable,
    /// Apply proof skipped an earlier event of the same origin.
    ApplyGap,
}

impl fmt::Display for CursorError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{self:?}")
    }
}

impl Error for CursorError {}
