//! The checked, durable lifecycle state machine for automatic indexes.

use std::collections::VecDeque;

use super::AutoIndexPhase;
use super::persistence::DecodedRecord;

/// Maximum number of lifecycle events retained for the dashboard/audit view.
pub(crate) const MAX_AUDIT_EVENTS: usize = 4_096;

/// An invisible index is retained for this long before its physical drop.
/// Visibility is changed immediately; this delay only protects a possible
/// rollback from an overly-sensitive monitor.
pub(crate) const INVISIBLE_RETENTION_MILLIS: u64 = 24 * 60 * 60 * 1_000;
/// Failed candidates remain blocklisted for one week.
pub(crate) const BLOCKLIST_RETENTION_MILLIS: u64 = 7 * 24 * 60 * 60 * 1_000;
/// Unused active indexes use the same seven-day retirement window.
pub(crate) const UNUSED_RETIREMENT_MILLIS: u64 = BLOCKLIST_RETENTION_MILLIS;

/// The externally useful names for the durable lifecycle states.
///
/// `AutoIndexPhase` also contains the planner's pre-build stages.  This view
/// deliberately names the states that have operational consequences and is
/// persisted through the event stream rather than inferred from a process
/// local monitor.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LifecycleStatus {
    Invisible,
    Building,
    Verified,
    Active,
    Pinned,
    Reverted,
    Retired,
    Blocklisted,
}

/// The identity bound to every lifecycle record.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct LifecycleIdentity {
    collection: String,
    index: String,
    generation: u64,
    schema: u64,
    sequence: u64,
}

impl LifecycleIdentity {
    /// Creates an identity, rejecting empty or oversized names and sequence 0.
    pub(crate) fn new(
        collection: impl Into<String>,
        index: impl Into<String>,
        generation: u64,
        schema: u64,
        sequence: u64,
    ) -> Result<Self, LifecycleError> {
        let identity = Self {
            collection: collection.into(),
            index: index.into(),
            generation,
            schema,
            sequence,
        };
        validate_name(&identity.collection)?;
        validate_name(&identity.index)?;
        if sequence == 0 {
            return Err(LifecycleError::InvalidIdentity);
        }
        Ok(identity)
    }
    pub(crate) fn collection(&self) -> &str {
        &self.collection
    }
    pub(crate) fn index(&self) -> &str {
        &self.index
    }
    pub(crate) const fn generation(&self) -> u64 {
        self.generation
    }
    pub(crate) const fn schema(&self) -> u64 {
        self.schema
    }
    pub(crate) const fn sequence(&self) -> u64 {
        self.sequence
    }
}

fn validate_name(name: &str) -> Result<(), LifecycleError> {
    if name.is_empty() || name.len() > super::persistence::MAX_NAME_BYTES {
        return Err(LifecycleError::InvalidIdentity);
    }
    if !name.bytes().all(|byte| byte.is_ascii_graphic() || byte == b' ') {
        return Err(LifecycleError::InvalidIdentity);
    }
    Ok(())
}

/// The durable operation represented by a lifecycle record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LifecycleOperation {
    Observe,
    Select,
    Build,
    Watermark,
    Verify,
    Visible,
    Revert,
    Retire,
    Merge,
    /// Pin the active index for the affected plan templates.
    Pin,
    /// Remove a pin without changing physical visibility.
    Unpin,
    /// Abandon a candidate and blocklist its shape for the cooldown period.
    Blocklist,
}

impl LifecycleOperation {
    pub(crate) const fn code(self) -> u8 {
        match self {
            Self::Observe => 1,
            Self::Select => 2,
            Self::Build => 3,
            Self::Watermark => 4,
            Self::Verify => 5,
            Self::Visible => 6,
            Self::Revert => 7,
            Self::Retire => 8,
            Self::Merge => 9,
            Self::Pin => 10,
            Self::Unpin => 11,
            Self::Blocklist => 12,
        }
    }
    pub(crate) const fn from_code(code: u8) -> Option<Self> {
        Some(match code {
            1 => Self::Observe,
            2 => Self::Select,
            3 => Self::Build,
            4 => Self::Watermark,
            5 => Self::Verify,
            6 => Self::Visible,
            7 => Self::Revert,
            8 => Self::Retire,
            9 => Self::Merge,
            10 => Self::Pin,
            11 => Self::Unpin,
            12 => Self::Blocklist,
            _ => return None,
        })
    }
}

/// A state transition suitable for durable audit evidence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct LifecycleEvent {
    pub(crate) identity: LifecycleIdentity,
    pub(crate) operation: LifecycleOperation,
    pub(crate) from: AutoIndexPhase,
    pub(crate) to: AutoIndexPhase,
    pub(crate) watermark: Option<u64>,
    pub(crate) checksum: [u8; 32],
    /// Digest of the candidate/evidence attached to this transition.
    pub(crate) candidate_digest: [u8; 32],
    /// Digest of verifier/monitor evidence for this transition.
    pub(crate) evidence_digest: [u8; 32],
    /// Wall-clock timestamp supplied by the durable coordinator, in millis.
    pub(crate) at_millis: u64,
}

/// Refusal from the lifecycle state machine.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LifecycleError {
    InvalidIdentity,
    InvalidTransition,
    MissingWatermark,
    StaleWatermark,
    SequenceConflict,
    CorruptRecord,
    StaleTimestamp,
    TimerNotElapsed,
}

/// Per-index state recovered from the lifecycle journal.
#[derive(Clone, Debug)]
pub(crate) struct LifecycleState {
    identity: LifecycleIdentity,
    phase: AutoIndexPhase,
    watermark: Option<u64>,
    last_sequence: u64,
    last_event: Option<LifecycleEvent>,
    events: VecDeque<LifecycleEvent>,
    pinned: bool,
    blocked_until: Option<u64>,
    invisible_since: Option<u64>,
    last_at_millis: u64,
}

impl LifecycleState {
    pub(crate) fn new(identity: LifecycleIdentity) -> Self {
        Self {
            identity,
            phase: AutoIndexPhase::Observing,
            watermark: None,
            last_sequence: 0,
            last_event: None,
            events: VecDeque::new(),
            pinned: false,
            blocked_until: None,
            invisible_since: None,
            last_at_millis: 0,
        }
    }
    pub(crate) const fn phase(&self) -> AutoIndexPhase {
        self.phase
    }
    pub(crate) const fn watermark(&self) -> Option<u64> {
        self.watermark
    }
    pub(crate) const fn last_sequence(&self) -> u64 {
        self.last_sequence
    }
    pub(crate) fn identity(&self) -> &LifecycleIdentity {
        &self.identity
    }
    pub(crate) fn events(&self) -> impl Iterator<Item = &LifecycleEvent> {
        self.events.iter()
    }
    pub(crate) const fn pinned(&self) -> bool {
        self.pinned
    }
    pub(crate) const fn invisible_since(&self) -> Option<u64> {
        self.invisible_since
    }
    pub(crate) const fn blocked_until(&self) -> Option<u64> {
        self.blocked_until
    }
    pub(crate) fn status(&self, now_millis: u64) -> LifecycleStatus {
        if self.blocklisted_at(now_millis) {
            return LifecycleStatus::Blocklisted;
        }
        match self.phase {
            AutoIndexPhase::Building => LifecycleStatus::Building,
            AutoIndexPhase::Shadowing => LifecycleStatus::Verified,
            AutoIndexPhase::Active if self.pinned => LifecycleStatus::Pinned,
            AutoIndexPhase::Active => LifecycleStatus::Active,
            AutoIndexPhase::Invisible => {
                if self.invisible_since.is_some() {
                    LifecycleStatus::Reverted
                } else {
                    LifecycleStatus::Invisible
                }
            }
            AutoIndexPhase::Abandoned => LifecycleStatus::Retired,
            _ => LifecycleStatus::Invisible,
        }
    }
    pub(crate) fn blocklisted_at(&self, now_millis: u64) -> bool {
        self.blocked_until.is_some_and(|until| now_millis < until)
    }
    /// Whether the planner may use this index at the supplied durable time.
    /// A reverted, building, retired, or blocklisted index is always hidden.
    pub(crate) fn can_serve(&self, now_millis: u64) -> bool {
        self.phase == AutoIndexPhase::Active && !self.blocklisted_at(now_millis)
    }
    pub(crate) fn is_invisible(&self) -> bool {
        !matches!(self.phase, AutoIndexPhase::Active)
    }
    pub(crate) fn retirement_due(&self, now_millis: u64) -> bool {
        self.invisible_since
            .is_some_and(|since| now_millis >= since.saturating_add(INVISIBLE_RETENTION_MILLIS))
    }
    pub(crate) fn unused_retirement_due(&self, now_millis: u64) -> bool {
        self.last_event.as_ref().is_some_and(|event| {
            self.phase == AutoIndexPhase::Active
                && now_millis >= event.at_millis.saturating_add(UNUSED_RETIREMENT_MILLIS)
        })
    }

    /// Applies one coordinator transition without requiring a storage adapter.
    /// The persistence adapter uses the same path after decoding a record.
    pub(crate) fn transition(
        &mut self,
        operation: LifecycleOperation,
        to: AutoIndexPhase,
        watermark: Option<u64>,
        sequence: u64,
    ) -> Result<bool, LifecycleError> {
        self.transition_at(operation, to, watermark, sequence, 0)
    }

    /// Applies a coordinator transition with a durable timestamp.  The
    /// timestamp is needed for restart-safe 24-hour and seven-day decisions.
    pub(crate) fn transition_at(
        &mut self,
        operation: LifecycleOperation,
        to: AutoIndexPhase,
        watermark: Option<u64>,
        sequence: u64,
        at_millis: u64,
    ) -> Result<bool, LifecycleError> {
        let identity = LifecycleIdentity::new(
            self.identity.collection.clone(),
            self.identity.index.clone(),
            self.identity.generation,
            self.identity.schema,
            sequence,
        )?;
        let record = DecodedRecord {
            identity,
            operation,
            from: self.phase,
            to,
            watermark,
            candidate_digest: [0; 32],
            evidence_digest: [0; 32],
            checksum: [0; 32],
            at_millis,
        };
        self.apply(&record)
    }

    /// Applies one decoded record. Exact duplicate delivery is idempotent.
    pub(crate) fn apply(&mut self, record: &DecodedRecord) -> Result<bool, LifecycleError> {
        if record.identity.collection != self.identity.collection
            || record.identity.index != self.identity.index
            || record.identity.generation != self.identity.generation
            || record.identity.schema != self.identity.schema
        {
            return Err(LifecycleError::InvalidIdentity);
        }
        let event = record.event();
        if record.identity.sequence == self.last_sequence {
            return if self.last_event.as_ref() == Some(&event) {
                Ok(false)
            } else {
                Err(LifecycleError::SequenceConflict)
            };
        }
        if record.identity.sequence < self.last_sequence {
            return Err(LifecycleError::SequenceConflict);
        }
        if record.at_millis != 0
            && self.last_at_millis != 0
            && record.at_millis < self.last_at_millis
        {
            return Err(LifecycleError::StaleTimestamp);
        }
        validate_transition(
            self.phase,
            record.operation,
            record.from,
            record.to,
            self.watermark,
            record.watermark,
        )?;
        if record.operation == LifecycleOperation::Retire && !self.retirement_due(record.at_millis)
        {
            return Err(LifecycleError::TimerNotElapsed);
        }
        self.phase = record.to;
        self.watermark = record.watermark.or(self.watermark);
        self.last_sequence = record.identity.sequence;
        if record.at_millis != 0 {
            self.last_at_millis = record.at_millis;
        }
        match record.operation {
            LifecycleOperation::Pin => self.pinned = true,
            LifecycleOperation::Unpin => self.pinned = false,
            LifecycleOperation::Revert => {
                self.pinned = false;
                self.invisible_since = (record.at_millis != 0).then_some(record.at_millis);
            }
            LifecycleOperation::Retire => self.invisible_since = None,
            LifecycleOperation::Blocklist => {
                self.pinned = false;
                self.blocked_until = record.at_millis.checked_add(BLOCKLIST_RETENTION_MILLIS);
            }
            _ => {}
        }
        self.last_event = Some(event.clone());
        self.events.push_back(event);
        while self.events.len() > MAX_AUDIT_EVENTS {
            self.events.pop_front();
        }
        Ok(true)
    }
}

fn validate_transition(
    current: AutoIndexPhase,
    operation: LifecycleOperation,
    from: AutoIndexPhase,
    to: AutoIndexPhase,
    previous_watermark: Option<u64>,
    watermark: Option<u64>,
) -> Result<(), LifecycleError> {
    if from != current {
        return Err(LifecycleError::InvalidTransition);
    }
    let valid = match operation {
        LifecycleOperation::Observe => current == AutoIndexPhase::Observing && to == current,
        LifecycleOperation::Select => {
            matches!(current, AutoIndexPhase::Observing | AutoIndexPhase::Compressed)
                && to == AutoIndexPhase::Selected
        }
        LifecycleOperation::Build => {
            current == AutoIndexPhase::Selected && to == AutoIndexPhase::Building
        }
        LifecycleOperation::Watermark => current == AutoIndexPhase::Building && to == current,
        LifecycleOperation::Verify => {
            current == AutoIndexPhase::Building && to == AutoIndexPhase::Shadowing
        }
        LifecycleOperation::Visible => {
            current == AutoIndexPhase::Shadowing && to == AutoIndexPhase::Active
        }
        LifecycleOperation::Revert => {
            current == AutoIndexPhase::Active && to == AutoIndexPhase::Invisible
        }
        LifecycleOperation::Retire => {
            current == AutoIndexPhase::Invisible && to == AutoIndexPhase::Abandoned
        }
        LifecycleOperation::Merge => current == AutoIndexPhase::Active && to == current,
        LifecycleOperation::Pin | LifecycleOperation::Unpin => {
            current == AutoIndexPhase::Active && to == current
        }
        LifecycleOperation::Blocklist => {
            matches!(current, AutoIndexPhase::Shadowing | AutoIndexPhase::Invisible)
                && to == AutoIndexPhase::Abandoned
        }
    };
    if !valid {
        return Err(LifecycleError::InvalidTransition);
    }
    if matches!(
        operation,
        LifecycleOperation::Build | LifecycleOperation::Watermark | LifecycleOperation::Verify
    ) && watermark.is_none()
    {
        return Err(LifecycleError::MissingWatermark);
    }
    if let (Some(previous), Some(next)) = (previous_watermark, watermark) {
        if next < previous || (operation == LifecycleOperation::Watermark && next == previous) {
            return Err(LifecycleError::StaleWatermark);
        }
    }
    Ok(())
}

/// The bounded, auditable view returned after journal replay.
#[derive(Clone, Debug)]
pub(crate) struct AuditView {
    pub(crate) events: VecDeque<LifecycleEvent>,
}
impl AuditView {
    pub(crate) fn events(&self) -> impl Iterator<Item = &LifecycleEvent> {
        self.events.iter()
    }
    pub(crate) fn len(&self) -> usize {
        self.events.len()
    }
}

/// Applies a record to a state, creating its identity on first observation.
pub(crate) fn apply_record(
    state: &mut Option<LifecycleState>,
    record: &DecodedRecord,
) -> Result<bool, LifecycleError> {
    if state.is_none() {
        *state = Some(LifecycleState::new(record.identity.clone()));
    }
    state.as_mut().expect("state initialized").apply(record)
}

#[cfg(test)]
mod tests {
    use super::{
        AutoIndexPhase, LifecycleError, LifecycleIdentity, LifecycleOperation, LifecycleState,
        validate_transition,
    };
    fn id(sequence: u64) -> LifecycleIdentity {
        LifecycleIdentity::new("users", "idx_users", 1, 7, sequence).unwrap()
    }
    #[test]
    fn invalid_transition_is_rejected() {
        assert_eq!(
            validate_transition(
                AutoIndexPhase::Observing,
                LifecycleOperation::Visible,
                AutoIndexPhase::Observing,
                AutoIndexPhase::Active,
                None,
                None
            ),
            Err(LifecycleError::InvalidTransition)
        );
    }
    #[test]
    fn identity_and_empty_audit_are_available() {
        let state = LifecycleState::new(id(1));
        assert_eq!(state.identity().collection(), "users");
        assert_eq!(state.events().count(), 0);
    }
}
