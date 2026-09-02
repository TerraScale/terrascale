//! Globally durable deletion workflow domain (gap-sweep row 40).
//!
//! The workspace charter (`GOAL.md` "Compliance and retention baseline")
//! requires a verified deletion request to remove ordinary live personal
//! data, indexes, caches, derived views, and active-region copies within
//! thirty days, immutable backup copies to expire through normal rotation
//! within ninety days, and legal, security, payment, tax, or fraud holds to
//! retain only the required fields, record the authority and expiry, and
//! block ordinary access. The charter's risk table names the mitigation
//! exactly: deletion tracked as a globally durable workflow with per-copy
//! receipts, a thirty-day live deadline, ninety-day backup expiry, and
//! explicit legal holds.
//!
//! This module is the typed domain for that workflow. It follows the same
//! shape as [`crate::usage`]: pure durable facts, typed errors, idempotent
//! indexes, and a canonical checksummed encoding a storage layer may
//! persist. It deliberately has no WAL, storage, transport, Dashboard, or
//! TBEP dependency — wiring the workflow into the storage tiers, backup
//! rotation, and the public contract is later work in those modules.
//!
//! The state machine is `Requested -> Admitted -> Executing -> Verified ->
//! Complete`, with `Failed` as the only other terminal state. Admission is
//! `GlobalDurable`-shaped (the charter's "`RegionalDurable` succeeds in every
//! selected active Region"): the deletion request must be durably recorded
//! by every selected active Region before any per-copy execution begins, so
//! no Region can silently miss the deletion. Execution records one receipt
//! per planned copy — live Region copies, indexes, caches, derived views,
//! and immutable backup rotation. Verification refuses while an unexpired
//! hold exists, refuses receipts that predate the last hold's end (the held
//! fields were retained, not deleted, during the hold), and fails the
//! workflow terminally when a copy crossed its deadline without hold
//! coverage.
//!
//! All times are caller-supplied Unix milliseconds; the domain never reads
//! a wall clock, so every transition is deterministic under injected times.
//! Receipts name copy identities, outcomes, and timestamps only — no
//! document content, field value, personal datum, path, or secret ever
//! enters a workflow record.

#![allow(clippy::missing_errors_doc)]

use std::{
    collections::{BTreeMap, BTreeSet},
    fmt,
};

use crate::types::{RegionId, TenantId};

/// The canonical version of the deletion workflow encoding.
pub const DELETION_FORMAT_VERSION: u16 = 1;

/// The checksum domain separator for the canonical workflow encoding.
pub const DELETION_CHECKSUM_DOMAIN: &[u8] = b"terrascale.deletion.workflow.v1\0";

/// The charter's live-copy deadline: thirty days.
///
/// Ordinary live personal data, indexes, caches, derived views, and
/// active-region copies must be removed within this many days of the
/// verified deletion request.
pub const LIVE_DEADLINE_DAYS: u64 = 30;

/// The charter's live-copy deadline in Unix milliseconds.
pub const LIVE_DEADLINE_MILLIS: u64 = LIVE_DEADLINE_DAYS * 24 * 60 * 60 * 1000;

/// The charter's immutable backup deadline: ninety days.
///
/// Immutable backup copies expire through normal rotation within this many
/// days of the verified deletion request, unless a recorded hold applies.
pub const BACKUP_DEADLINE_DAYS: u64 = 90;

/// The charter's immutable backup deadline in Unix milliseconds.
pub const BACKUP_DEADLINE_MILLIS: u64 = BACKUP_DEADLINE_DAYS * 24 * 60 * 60 * 1000;

/// The maximum length accepted for one logical deletion identifier.
const MAX_IDENTIFIER_BYTES: usize = 255;

/// The maximum number of copies one deletion plan may name.
///
/// A plan is bounded so a workflow record stays small and a caller cannot
/// admit an unbounded sweep through the domain layer.
pub const MAX_PLAN_TARGETS: usize = 1024;

/// The maximum number of retained fields one hold may declare.
pub const MAX_RETAINED_FIELDS: usize = 256;

/// The maximum number of holds one workflow may carry.
pub const MAX_HOLDS: usize = 256;

/// Typed failures in the deletion workflow domain.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DeletionError {
    /// An identifier was empty, too long, or contained forbidden text.
    InvalidIdentifier,
    /// The deletion request named no selected active Region.
    EmptyRegionSet,
    /// A Region appeared where only selected Regions are allowed.
    UnknownRegion,
    /// The identity verification timestamp postdates the request it verifies.
    InvalidVerification,
    /// Admission did not durably record every selected active Region.
    AdmissionNotGlobalDurable,
    /// The plan does not give every selected Region a live-copy target.
    PlanMissesRegion,
    /// The plan is empty or exceeds the bounded plan size.
    InvalidPlan,
    /// An admission timestamp precedes the request it admits.
    InvalidAdmissionTime,
    /// Work was attempted before `GlobalDurable` admission.
    NotAdmitted,
    /// The workflow already reached a terminal state.
    TerminalState,
    /// The transition does not exist in the workflow state machine.
    InvalidTransition,
    /// A receipt names a copy the admitted plan does not contain.
    CopyTargetNotPlanned,
    /// A receipt's outcome does not match its copy class.
    InvalidReceiptOutcome,
    /// A receipt's completion time precedes admission.
    InvalidReceiptTime,
    /// A hold's expiry does not postdate its recording.
    InvalidHoldWindow,
    /// A hold retains no fields or more fields than the bound allows.
    InvalidRetainedFields,
    /// A hold's recording time precedes the deletion request.
    InvalidHoldRecordingTime,
    /// A hold authority is already recorded with different facts.
    HoldConflict,
    /// The workflow already carries the bounded maximum number of holds.
    HoldLimitExceeded,
    /// A hold authority is not recorded on this workflow.
    UnknownHold,
    /// Verification cannot complete while an unexpired hold applies.
    HoldBlocksCompletion,
    /// A receipt predates the last hold's end, so it cannot prove the held
    /// fields were removed.
    ReceiptPredatesHoldEnd,
    /// Not every planned copy has a receipt yet.
    MissingReceipts,
    /// A copy crossed its deadline; the workflow fails terminally.
    DeadlineExceeded,
    /// The encoded workflow is truncated or has an unknown field value.
    MalformedEncoding,
    /// The encoded checksum does not match its canonical body.
    ChecksumMismatch,
    /// The bytes are valid but not the one canonical encoding.
    NonCanonicalEncoding,
    /// The workflow identity was reused for different facts.
    WorkflowConflict,
}

impl fmt::Display for DeletionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::InvalidIdentifier => "invalid deletion identifier",
            Self::EmptyRegionSet => "deletion request names no selected Region",
            Self::UnknownRegion => "deletion names a Region that is not selected",
            Self::InvalidVerification => "deletion verification time is invalid",
            Self::AdmissionNotGlobalDurable => {
                "deletion admission is not durable in every selected Region"
            }
            Self::PlanMissesRegion => "deletion plan misses a selected Region's live copy",
            Self::InvalidPlan => "deletion plan is empty or unbounded",
            Self::InvalidAdmissionTime => "deletion admission time precedes its request",
            Self::NotAdmitted => "deletion work precedes GlobalDurable admission",
            Self::TerminalState => "deletion workflow is terminal",
            Self::InvalidTransition => "invalid deletion workflow transition",
            Self::CopyTargetNotPlanned => "deletion receipt names an unplanned copy",
            Self::InvalidReceiptOutcome => "deletion receipt outcome mismatches its copy class",
            Self::InvalidReceiptTime => "deletion receipt precedes admission",
            Self::InvalidHoldWindow => "hold expiry does not postdate its recording",
            Self::InvalidRetainedFields => "hold retained-field set is empty or unbounded",
            Self::InvalidHoldRecordingTime => "hold recording precedes the deletion request",
            Self::HoldConflict => "hold authority is recorded with different facts",
            Self::HoldLimitExceeded => "deletion workflow holds exceed their bound",
            Self::UnknownHold => "hold authority is not recorded",
            Self::HoldBlocksCompletion => "an unexpired hold blocks deletion completion",
            Self::ReceiptPredatesHoldEnd => "deletion receipt predates the last hold's end",
            Self::MissingReceipts => "deletion copies remain unreceipted",
            Self::DeadlineExceeded => "deletion copy crossed its deadline",
            Self::MalformedEncoding => "malformed deletion workflow encoding",
            Self::ChecksumMismatch => "deletion workflow checksum mismatch",
            Self::NonCanonicalEncoding => "non-canonical deletion workflow encoding",
            Self::WorkflowConflict => "deletion workflow identity names different facts",
        })
    }
}

impl std::error::Error for DeletionError {}

/// A validated stable deletion-request identity used for idempotent retries.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DeletionRequestId(String);

impl DeletionRequestId {
    /// Creates a validated request identity.
    pub fn try_new(value: &str) -> Result<Self, DeletionError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical identity text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for DeletionRequestId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A validated identity of the personal-data subject whose data is deleted.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SubjectId(String);

impl SubjectId {
    /// Creates a validated subject identity.
    pub fn try_new(value: &str) -> Result<Self, DeletionError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical identity text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for SubjectId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A validated name of one index, cache, derived view, or backup cluster.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CopyName(String);

impl CopyName {
    /// Creates a validated copy name.
    pub fn try_new(value: &str) -> Result<Self, DeletionError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical name text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for CopyName {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A validated authority reference recorded on one hold.
///
/// The authority names the legal, security, payment, tax, or fraud basis
/// (for example a case or warrant reference). It is an opaque identifier
/// here; evaluating the authority itself belongs to the caller.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct HoldAuthorityId(String);

impl HoldAuthorityId {
    /// Creates a validated hold authority reference.
    pub fn try_new(value: &str) -> Result<Self, DeletionError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical authority text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for HoldAuthorityId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// One field a hold retains. Field names, never values.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct RetainedField(String);

impl RetainedField {
    /// Creates a validated retained-field name.
    pub fn try_new(value: &str) -> Result<Self, DeletionError> {
        validate_identifier(value)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical field name.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for RetainedField {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// The tenant and subject scope of one deletion request.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DeletionScope {
    tenant_id: TenantId,
    subject_id: SubjectId,
}

impl DeletionScope {
    /// Creates a fully scoped deletion identity.
    pub fn try_new(tenant_id: TenantId, subject_id: &str) -> Result<Self, DeletionError> {
        Ok(Self { tenant_id, subject_id: SubjectId::try_new(subject_id)? })
    }

    /// Returns the tenant identity.
    #[must_use]
    pub fn tenant_id(&self) -> &TenantId {
        &self.tenant_id
    }

    /// Returns the personal-data subject identity.
    #[must_use]
    pub fn subject_id(&self) -> &SubjectId {
        &self.subject_id
    }
}

/// The class of one copy a deletion must reach.
///
/// The first four classes carry the charter's thirty-day live deadline; the
/// backup class carries the ninety-day rotation deadline.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CopyClass {
    /// One selected active Region's copy of the ordinary live personal data.
    LiveRegion,
    /// One engine-owned index covering the deleted data.
    Index,
    /// One derived cache covering the deleted data.
    Cache,
    /// One materialized or derived view covering the deleted data.
    DerivedView,
    /// One immutable backup copy expiring through normal rotation.
    BackupRotation,
}

impl CopyClass {
    /// Returns every copy class in canonical order.
    #[must_use]
    pub const fn all() -> [Self; 5] {
        [Self::LiveRegion, Self::Index, Self::Cache, Self::DerivedView, Self::BackupRotation]
    }

    /// Returns the charter deadline for this class in Unix milliseconds.
    #[must_use]
    pub const fn deadline_millis(self) -> u64 {
        match self {
            Self::LiveRegion | Self::Index | Self::Cache | Self::DerivedView => {
                LIVE_DEADLINE_MILLIS
            }
            Self::BackupRotation => BACKUP_DEADLINE_MILLIS,
        }
    }
}

/// One copy a deletion plan names. Live copies carry their Region; the
/// remaining classes are database- or cluster-scoped names.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CopyTarget {
    class: CopyClass,
    region: Option<RegionId>,
    name: CopyName,
}

impl CopyTarget {
    /// Creates one live active-Region copy target.
    pub fn try_new_live(region: RegionId, name: &str) -> Result<Self, DeletionError> {
        Ok(Self {
            class: CopyClass::LiveRegion,
            region: Some(region),
            name: CopyName::try_new(name)?,
        })
    }

    /// Creates one non-Region copy target (index, cache, view, backup).
    pub fn try_new_scoped(class: CopyClass, name: &str) -> Result<Self, DeletionError> {
        if class == CopyClass::LiveRegion {
            return Err(DeletionError::InvalidIdentifier);
        }
        Ok(Self { class, region: None, name: CopyName::try_new(name)? })
    }

    /// Returns the copy class.
    #[must_use]
    pub const fn class(&self) -> CopyClass {
        self.class
    }

    /// Returns the Region of a live copy target.
    #[must_use]
    pub fn region(&self) -> Option<&RegionId> {
        self.region.as_ref()
    }

    /// Returns the copy name.
    #[must_use]
    pub fn name(&self) -> &CopyName {
        &self.name
    }
}

/// The recorded outcome for one copy.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CopyOutcome {
    /// The copy was removed from the live tiers.
    Removed,
    /// The copy left retention through normal rotation (backup class only).
    RotatedOut,
}

/// The per-copy receipt. Identities, outcome, and time only — no content.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CopyReceipt {
    target: CopyTarget,
    outcome: CopyOutcome,
    completed_at_millis: u64,
}

impl CopyReceipt {
    /// Creates one receipt for a planned copy.
    #[must_use]
    pub fn new(target: CopyTarget, outcome: CopyOutcome, completed_at_millis: u64) -> Self {
        Self { target, outcome, completed_at_millis }
    }

    /// Returns the copy this receipt proves removed.
    #[must_use]
    pub fn target(&self) -> &CopyTarget {
        &self.target
    }

    /// Returns the recorded outcome.
    #[must_use]
    pub const fn outcome(&self) -> CopyOutcome {
        self.outcome
    }

    /// Returns the completion time in Unix milliseconds.
    #[must_use]
    pub const fn completed_at_millis(&self) -> u64 {
        self.completed_at_millis
    }
}

/// The charter's hold families.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum HoldKind {
    /// A legal hold.
    Legal,
    /// A security hold.
    Security,
    /// A payment hold.
    Payment,
    /// A tax hold.
    Tax,
    /// A fraud hold.
    Fraud,
}

impl HoldKind {
    /// Returns every hold kind in canonical order.
    #[must_use]
    pub const fn all() -> [Self; 5] {
        [Self::Legal, Self::Security, Self::Payment, Self::Tax, Self::Fraud]
    }
}

/// One recorded hold: authority, window, and the only fields it retains.
///
/// The charter requires a hold to retain only the required fields, record
/// the authority and expiry, and block ordinary access. For payment, tax,
/// and fraud holds on accounting records, the retained fields must be the
/// stable pseudonymous accounting identifiers — the domain records the
/// declared set; replacing profile fields with pseudonyms at execution time
/// is the storage layer's obligation, not this domain's.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HoldRecord {
    kind: HoldKind,
    authority: HoldAuthorityId,
    recorded_at_millis: u64,
    expires_at_millis: u64,
    retained_fields: BTreeSet<RetainedField>,
}

impl HoldRecord {
    /// Creates one hold with a future expiry and a non-empty retained set.
    ///
    /// `retained_fields` is exactly the set the hold retains — nothing
    /// else survives the deletion while the hold applies.
    pub fn try_new(
        kind: HoldKind,
        authority: &str,
        recorded_at_millis: u64,
        expires_at_millis: u64,
        retained_fields: &[&str],
    ) -> Result<Self, DeletionError> {
        if expires_at_millis <= recorded_at_millis {
            return Err(DeletionError::InvalidHoldWindow);
        }
        let mut fields = BTreeSet::new();
        for field in retained_fields {
            fields.insert(RetainedField::try_new(field)?);
        }
        if fields.is_empty() || fields.len() > MAX_RETAINED_FIELDS {
            return Err(DeletionError::InvalidRetainedFields);
        }
        Ok(Self {
            kind,
            authority: HoldAuthorityId::try_new(authority)?,
            recorded_at_millis,
            expires_at_millis,
            retained_fields: fields,
        })
    }

    /// Returns the hold family.
    #[must_use]
    pub const fn kind(&self) -> HoldKind {
        self.kind
    }

    /// Returns the recorded authority reference.
    #[must_use]
    pub fn authority(&self) -> &HoldAuthorityId {
        &self.authority
    }

    /// Returns the recording time in Unix milliseconds.
    #[must_use]
    pub const fn recorded_at_millis(&self) -> u64 {
        self.recorded_at_millis
    }

    /// Returns the expiry time in Unix milliseconds.
    #[must_use]
    pub const fn expires_at_millis(&self) -> u64 {
        self.expires_at_millis
    }

    /// Returns the only fields this hold retains, in stable order.
    pub fn retained_fields(&self) -> impl Iterator<Item = &RetainedField> {
        self.retained_fields.iter()
    }

    /// Whether this hold still applies at `now_millis`.
    #[must_use]
    pub const fn is_active_at(&self, now_millis: u64) -> bool {
        now_millis < self.expires_at_millis
    }
}

/// The `GlobalDurable` admission fact: every selected active Region durably
/// recorded the deletion request before any copy execution began.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GlobalDurableAdmission {
    acknowledged_regions: BTreeSet<RegionId>,
    admitted_at_millis: u64,
}

impl GlobalDurableAdmission {
    /// Creates one admission from the Regions that durably recorded the
    /// request.
    #[must_use]
    pub fn new(acknowledged_regions: BTreeSet<RegionId>, admitted_at_millis: u64) -> Self {
        Self { acknowledged_regions, admitted_at_millis }
    }

    /// Returns every acknowledging Region in stable order.
    pub fn acknowledged_regions(&self) -> impl Iterator<Item = &RegionId> {
        self.acknowledged_regions.iter()
    }

    /// Returns the admission time in Unix milliseconds.
    #[must_use]
    pub const fn admitted_at_millis(&self) -> u64 {
        self.admitted_at_millis
    }
}

/// The workflow state machine.
///
/// `Requested -> Admitted -> Executing -> Verified -> Complete`, with
/// `Failed` terminal. A workflow enters `Executing` with its first receipt
/// and re-enters it whenever a hold or a superseding receipt invalidates a
/// prior verification.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum DeletionState {
    /// A verified deletion request is recorded and nothing more.
    Requested,
    /// `GlobalDurable` admission completed; the plan is fixed.
    Admitted,
    /// Per-copy execution has produced at least one receipt.
    Executing,
    /// Every planned copy is receipted within its deadline and no hold
    /// applies.
    Verified,
    /// Terminal success.
    Complete,
    /// Terminal failure.
    Failed(DeletionFailure),
}

impl DeletionState {
    /// Whether this state can no longer change.
    #[must_use]
    pub const fn is_terminal(self) -> bool {
        matches!(self, Self::Complete | Self::Failed(_))
    }
}

/// Why a deletion workflow failed terminally.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum DeletionFailure {
    /// A copy crossed its deadline without hold coverage. The violations
    /// stay queryable through
    /// [`DeletionWorkflow::deadline_violations_at`].
    DeadlineExceeded,
}

/// One deadline violation: the copy, its effective deadline, and the actual
/// completion time.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DeadlineViolation {
    target: CopyTarget,
    deadline_at_millis: u64,
    completed_at_millis: u64,
}

impl DeadlineViolation {
    /// Returns the copy that crossed its deadline.
    #[must_use]
    pub fn target(&self) -> &CopyTarget {
        &self.target
    }

    /// Returns the effective (hold-extended) deadline.
    #[must_use]
    pub const fn deadline_at_millis(&self) -> u64 {
        self.deadline_at_millis
    }

    /// Returns the actual completion time.
    #[must_use]
    pub const fn completed_at_millis(&self) -> u64 {
        self.completed_at_millis
    }
}

/// The outcome of recording one receipt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReceiptOutcome {
    /// The first receipt for this copy.
    Recorded,
    /// A later receipt replaced an earlier one for the same copy.
    Superseded,
    /// The receipt changed nothing — an exact duplicate or a stale replay.
    Duplicate,
}

/// The outcome of recording or ending one hold.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HoldOutcome {
    /// The hold was newly recorded.
    Recorded,
    /// The exact hold was already present; no state changed.
    Duplicate,
    /// The hold's end was newly recorded.
    Ended,
    /// The hold's end was already recorded; no state changed.
    AlreadyEnded,
}

/// The receipt of one successful verification.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Verification {
    receipt_count: usize,
    live_deadline_at_millis: u64,
    backup_deadline_at_millis: u64,
}

impl Verification {
    /// Returns how many planned copies are receipted.
    #[must_use]
    pub const fn receipt_count(&self) -> usize {
        self.receipt_count
    }

    /// Returns the absolute live deadline this verification enforced.
    #[must_use]
    pub const fn live_deadline_at_millis(&self) -> u64 {
        self.live_deadline_at_millis
    }

    /// Returns the absolute backup deadline this verification enforced.
    #[must_use]
    pub const fn backup_deadline_at_millis(&self) -> u64 {
        self.backup_deadline_at_millis
    }
}

/// The receipt of one completed deletion workflow.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Completion {
    request_id: DeletionRequestId,
    completed_at_millis: u64,
    receipt_count: usize,
}

impl Completion {
    /// Returns the completed workflow's identity.
    #[must_use]
    pub fn request_id(&self) -> &DeletionRequestId {
        &self.request_id
    }

    /// Returns the completion time in Unix milliseconds.
    #[must_use]
    pub const fn completed_at_millis(&self) -> u64 {
        self.completed_at_millis
    }

    /// Returns how many planned copies are receipted.
    #[must_use]
    pub const fn receipt_count(&self) -> usize {
        self.receipt_count
    }
}

/// One globally durable deletion workflow.
///
/// All times are Unix milliseconds supplied by the caller, so every
/// transition is deterministic under injected clocks and honest about skew:
/// the domain never reads a wall clock.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DeletionWorkflow {
    request_id: DeletionRequestId,
    scope: DeletionScope,
    verified_at_millis: u64,
    requested_at_millis: u64,
    selected_regions: BTreeSet<RegionId>,
    admission: Option<GlobalDurableAdmission>,
    plan: BTreeSet<CopyTarget>,
    receipts: BTreeMap<CopyTarget, CopyReceipt>,
    holds: BTreeMap<HoldAuthorityId, HoldRecord>,
    hold_ends: BTreeMap<HoldAuthorityId, u64>,
    state: DeletionState,
    verified_at: Option<u64>,
    completed_at: Option<u64>,
}

impl DeletionWorkflow {
    /// Records one verified deletion request.
    ///
    /// `verified_at_millis` is when the requester's identity was verified;
    /// it must not postdate the request itself. A workflow exists only for
    /// verified requests — the domain cannot verify identity, so the caller
    /// supplies and owns that fact.
    pub fn request(
        request_id: DeletionRequestId,
        scope: DeletionScope,
        selected_regions: BTreeSet<RegionId>,
        verified_at_millis: u64,
        requested_at_millis: u64,
    ) -> Result<Self, DeletionError> {
        if selected_regions.is_empty() {
            return Err(DeletionError::EmptyRegionSet);
        }
        if verified_at_millis > requested_at_millis {
            return Err(DeletionError::InvalidVerification);
        }
        Ok(Self {
            request_id,
            scope,
            verified_at_millis,
            requested_at_millis,
            selected_regions,
            admission: None,
            plan: BTreeSet::new(),
            receipts: BTreeMap::new(),
            holds: BTreeMap::new(),
            hold_ends: BTreeMap::new(),
            state: DeletionState::Requested,
            verified_at: None,
            completed_at: None,
        })
    }

    /// Returns the workflow identity.
    #[must_use]
    pub fn request_id(&self) -> &DeletionRequestId {
        &self.request_id
    }

    /// Returns the deletion scope.
    #[must_use]
    pub fn scope(&self) -> &DeletionScope {
        &self.scope
    }

    /// Returns the current state.
    #[must_use]
    pub const fn state(&self) -> DeletionState {
        self.state
    }

    /// Returns every selected active Region in stable order.
    pub fn selected_regions(&self) -> impl Iterator<Item = &RegionId> {
        self.selected_regions.iter()
    }

    /// Returns the planned copies in stable order.
    pub fn plan(&self) -> impl Iterator<Item = &CopyTarget> {
        self.plan.iter()
    }

    /// Returns the recorded receipts in stable target order.
    pub fn receipts(&self) -> impl Iterator<Item = &CopyReceipt> {
        self.receipts.values()
    }

    /// Returns every recorded hold in stable authority order.
    pub fn holds(&self) -> impl Iterator<Item = &HoldRecord> {
        self.holds.values()
    }

    /// Returns the request time in Unix milliseconds.
    #[must_use]
    pub const fn requested_at_millis(&self) -> u64 {
        self.requested_at_millis
    }

    /// Admits the workflow for `GlobalDurable` execution.
    ///
    /// Every selected active Region must have durably recorded the request
    /// (`acknowledged` is a superset of the selected Regions and names no
    /// other Region), and the plan must give every selected Region one
    /// live-copy target. An acknowledgement from an unselected Region, or a
    /// plan target naming one, fails closed: adding a Region later cannot
    /// silently move regulated data into it — a new workflow is required
    /// for a new Region's copies.
    pub fn admit(
        &mut self,
        admission: GlobalDurableAdmission,
        plan: BTreeSet<CopyTarget>,
    ) -> Result<(), DeletionError> {
        match self.state {
            DeletionState::Requested => {}
            DeletionState::Complete | DeletionState::Failed(_) => {
                return Err(DeletionError::TerminalState);
            }
            _ => return Err(DeletionError::InvalidTransition),
        }
        if admission.admitted_at_millis() < self.requested_at_millis {
            return Err(DeletionError::InvalidAdmissionTime);
        }
        for region in admission.acknowledged_regions() {
            if !self.selected_regions.contains(region) {
                return Err(DeletionError::UnknownRegion);
            }
        }
        for region in &self.selected_regions {
            let acknowledged =
                admission.acknowledged_regions().any(|candidate| candidate == region);
            if !acknowledged {
                return Err(DeletionError::AdmissionNotGlobalDurable);
            }
        }
        if plan.is_empty() || plan.len() > MAX_PLAN_TARGETS {
            return Err(DeletionError::InvalidPlan);
        }
        for target in &plan {
            if target.region().is_some_and(|region| !self.selected_regions.contains(region)) {
                return Err(DeletionError::UnknownRegion);
            }
        }
        for region in &self.selected_regions {
            let covered = plan.iter().any(|target| {
                target.class() == CopyClass::LiveRegion && target.region() == Some(region)
            });
            if !covered {
                return Err(DeletionError::PlanMissesRegion);
            }
        }
        self.admission = Some(admission);
        self.plan = plan;
        self.state = DeletionState::Admitted;
        Ok(())
    }

    /// Records one per-copy receipt, idempotently.
    ///
    /// The target must be planned. Backup-rotation targets record
    /// [`CopyOutcome::RotatedOut`] (normal rotation); live classes record
    /// [`CopyOutcome::Removed`]. An exact duplicate — or a stale replay
    /// older than the recorded receipt — changes nothing; a later receipt
    /// for the same copy supersedes the earlier one. Because the outcome is
    /// fixed by the copy class, two receipts for one target at one
    /// completion time are always the same receipt.
    pub fn record_receipt(
        &mut self,
        receipt: CopyReceipt,
    ) -> Result<ReceiptOutcome, DeletionError> {
        match self.state {
            DeletionState::Admitted | DeletionState::Executing | DeletionState::Verified => {}
            DeletionState::Requested => return Err(DeletionError::NotAdmitted),
            DeletionState::Complete | DeletionState::Failed(_) => {
                return Err(DeletionError::TerminalState);
            }
        }
        let admitted_at =
            self.admission.as_ref().map_or(0, GlobalDurableAdmission::admitted_at_millis);
        let expected_outcome = match receipt.target().class() {
            CopyClass::BackupRotation => CopyOutcome::RotatedOut,
            _ => CopyOutcome::Removed,
        };
        if receipt.outcome() != expected_outcome {
            return Err(DeletionError::InvalidReceiptOutcome);
        }
        if !self.plan.contains(receipt.target()) {
            return Err(DeletionError::CopyTargetNotPlanned);
        }
        if receipt.completed_at_millis() < admitted_at {
            return Err(DeletionError::InvalidReceiptTime);
        }
        let outcome = match self.receipts.get(receipt.target()) {
            Some(existing) if existing == &receipt => return Ok(ReceiptOutcome::Duplicate),
            Some(existing) => {
                if receipt.completed_at_millis() > existing.completed_at_millis() {
                    ReceiptOutcome::Superseded
                } else {
                    return Ok(ReceiptOutcome::Duplicate);
                }
            }
            None => ReceiptOutcome::Recorded,
        };
        if self.state == DeletionState::Verified {
            self.state = DeletionState::Executing;
            self.verified_at = None;
        }
        self.receipts.insert(receipt.target().clone(), receipt);
        if self.state == DeletionState::Admitted {
            self.state = DeletionState::Executing;
        }
        Ok(outcome)
    }

    /// Records one hold, idempotently by authority.
    ///
    /// The same authority with the same facts is a no-op; different facts
    /// under the same authority conflict. Recording a hold after
    /// verification invalidates the verification, because the held fields
    /// were retained.
    pub fn record_hold(&mut self, record: HoldRecord) -> Result<HoldOutcome, DeletionError> {
        match self.state {
            DeletionState::Requested
            | DeletionState::Admitted
            | DeletionState::Executing
            | DeletionState::Verified => {}
            DeletionState::Complete | DeletionState::Failed(_) => {
                return Err(DeletionError::TerminalState);
            }
        }
        if record.recorded_at_millis() < self.requested_at_millis {
            return Err(DeletionError::InvalidHoldRecordingTime);
        }
        if self.holds.len() >= MAX_HOLDS && !self.holds.contains_key(record.authority()) {
            return Err(DeletionError::HoldLimitExceeded);
        }
        match self.holds.get(record.authority()) {
            Some(existing) if existing == &record => return Ok(HoldOutcome::Duplicate),
            Some(_) => return Err(DeletionError::HoldConflict),
            None => {}
        }
        self.holds.insert(record.authority().clone(), record);
        if self.state == DeletionState::Verified {
            self.state = DeletionState::Executing;
            self.verified_at = None;
        }
        Ok(HoldOutcome::Recorded)
    }

    /// Ends one hold — explicit release or expiry processing — idempotently.
    ///
    /// A release before expiry ends the hold at `now_millis`; an already
    /// expired hold ends at its expiry. Ending a hold does not complete the
    /// workflow: the retained fields must now be deleted, so receipts must
    /// postdate the hold's end before verification can pass again.
    pub fn end_hold(
        &mut self,
        authority: &HoldAuthorityId,
        now_millis: u64,
    ) -> Result<HoldOutcome, DeletionError> {
        match self.state {
            DeletionState::Requested
            | DeletionState::Admitted
            | DeletionState::Executing
            | DeletionState::Verified => {}
            DeletionState::Complete | DeletionState::Failed(_) => {
                return Err(DeletionError::TerminalState);
            }
        }
        if self.hold_ends.contains_key(authority) {
            return Ok(HoldOutcome::AlreadyEnded);
        }
        let hold = self.holds.get(authority).ok_or(DeletionError::UnknownHold)?;
        let expires_at = hold.expires_at_millis();
        let recorded_at = hold.recorded_at_millis();
        if now_millis < recorded_at {
            return Err(DeletionError::InvalidHoldWindow);
        }
        let end = now_millis.min(expires_at);
        self.hold_ends.insert(authority.clone(), end);
        if self.state == DeletionState::Verified {
            self.state = DeletionState::Executing;
            self.verified_at = None;
        }
        Ok(HoldOutcome::Ended)
    }

    /// Verifies every planned copy is receipted within its deadline and no
    /// hold applies, moving to `Verified`.
    ///
    /// Three refusals keep the charter honest:
    ///
    /// - [`DeletionError::MissingReceipts`] — planned copies remain
    ///   unreceipted (query them through [`Self::missing_targets`]).
    /// - [`DeletionError::HoldBlocksCompletion`] — a hold with no recorded
    ///   end is still unexpired at `now`; ordinary access stays blocked.
    /// - [`DeletionError::ReceiptPredatesHoldEnd`] — a receipt predates the
    ///   last hold's effective end, so the retained fields were not proven
    ///   deleted; re-execute and record fresh receipts.
    ///
    /// A deadline violation instead fails the workflow terminally with
    /// [`DeletionError::DeadlineExceeded`]: a crossed deadline is a
    /// compliance fact to surface, never a silent success. The violations
    /// stay queryable through [`Self::deadline_violations_at`] after the
    /// failure.
    pub fn verify(&mut self, now_millis: u64) -> Result<Verification, DeletionError> {
        match self.state {
            DeletionState::Admitted | DeletionState::Executing | DeletionState::Verified => {}
            DeletionState::Requested => return Err(DeletionError::NotAdmitted),
            DeletionState::Complete | DeletionState::Failed(_) => {
                return Err(DeletionError::TerminalState);
            }
        }
        if self.missing_targets().next().is_some() {
            return Err(DeletionError::MissingReceipts);
        }
        if !self.unexpired_holds(now_millis).is_empty() {
            return Err(DeletionError::HoldBlocksCompletion);
        }
        let last_hold_end = self.last_hold_end(now_millis);
        if self.receipts.values().any(|receipt| receipt.completed_at_millis() <= last_hold_end) {
            return Err(DeletionError::ReceiptPredatesHoldEnd);
        }
        if !self.deadline_violations_at(now_millis).is_empty() {
            self.state = DeletionState::Failed(DeletionFailure::DeadlineExceeded);
            return Err(DeletionError::DeadlineExceeded);
        }
        self.state = DeletionState::Verified;
        self.verified_at = Some(now_millis);
        Ok(Verification {
            receipt_count: self.receipts.len(),
            live_deadline_at_millis: self.requested_at_millis.saturating_add(LIVE_DEADLINE_MILLIS),
            backup_deadline_at_millis: self
                .requested_at_millis
                .saturating_add(BACKUP_DEADLINE_MILLIS),
        })
    }

    /// Completes a verified workflow.
    ///
    /// Re-checks holds so a hold recorded between verification and
    /// completion cannot slip through.
    pub fn complete(&mut self, now_millis: u64) -> Result<Completion, DeletionError> {
        match self.state {
            DeletionState::Verified => {}
            DeletionState::Requested | DeletionState::Admitted | DeletionState::Executing => {
                return Err(DeletionError::InvalidTransition);
            }
            DeletionState::Complete | DeletionState::Failed(_) => {
                return Err(DeletionError::TerminalState);
            }
        }
        let verified_at = self.verified_at.ok_or(DeletionError::InvalidTransition)?;
        if now_millis < verified_at {
            return Err(DeletionError::InvalidTransition);
        }
        if !self.unexpired_holds(now_millis).is_empty() {
            return Err(DeletionError::HoldBlocksCompletion);
        }
        self.state = DeletionState::Complete;
        self.completed_at = Some(now_millis);
        Ok(Completion {
            request_id: self.request_id.clone(),
            completed_at_millis: now_millis,
            receipt_count: self.receipts.len(),
        })
    }

    /// Returns the planned copies with no receipt yet.
    pub fn missing_targets(&self) -> impl Iterator<Item = &CopyTarget> {
        self.plan.iter().filter(|target| !self.receipts.contains_key(*target))
    }

    /// Returns the holds still applying at `now_millis`.
    #[must_use]
    pub fn unexpired_holds(&self, now_millis: u64) -> Vec<&HoldRecord> {
        self.holds
            .values()
            .filter(|record| {
                !self.hold_ends.contains_key(record.authority()) && record.is_active_at(now_millis)
            })
            .collect()
    }

    /// Whether ordinary access to the retained data stays blocked at `now`.
    #[must_use]
    pub fn ordinary_access_blocked(&self, now_millis: u64) -> bool {
        !self.unexpired_holds(now_millis).is_empty()
    }

    /// Returns the union of fields unexpired holds retain, in stable order.
    ///
    /// These are the only fields that survive the deletion while a hold
    /// applies.
    #[must_use]
    pub fn retained_fields(&self, now_millis: u64) -> BTreeSet<RetainedField> {
        self.unexpired_holds(now_millis)
            .into_iter()
            .flat_map(|hold| hold.retained_fields().cloned())
            .collect()
    }

    /// The last hold's effective end at `now`: the greatest end among every
    /// ended or expired hold. Zero when no hold ever ended. A hold with no
    /// recorded end that is still unexpired does not count — it blocks
    /// verification outright instead.
    #[must_use]
    pub fn last_hold_end(&self, now_millis: u64) -> u64 {
        self.holds
            .values()
            .filter_map(|hold| self.hold_effective_end(hold, now_millis))
            .max()
            .unwrap_or(0)
    }

    /// A hold's effective end: its recorded end (release or expiry
    /// processing), or its expiry when `now` has already passed it.
    /// `None` while the hold is still open and unexpired.
    fn hold_effective_end(&self, hold: &HoldRecord, now_millis: u64) -> Option<u64> {
        if let Some(end) = self.hold_ends.get(hold.authority()) {
            return Some(*end);
        }
        (hold.expires_at_millis() <= now_millis).then_some(hold.expires_at_millis())
    }

    /// The total time holds have covered this workflow, clipped to
    /// `[requested_at, at_millis]`. Overlapping holds count once.
    ///
    /// This is the charter's "unless a recorded legal hold applies" made
    /// arithmetic: a copy's deadline extends by exactly the covered hold
    /// time. A hold still open at `now_millis` counts as covering through
    /// `at_millis`, because it applies until it ends.
    #[must_use]
    pub fn hold_coverage_millis(&self, now_millis: u64, at_millis: u64) -> u64 {
        let mut intervals: Vec<(u64, u64)> = self
            .holds
            .values()
            .map(|hold| {
                let start = hold.recorded_at_millis().max(self.requested_at_millis);
                let end =
                    self.hold_effective_end(hold, now_millis).unwrap_or(u64::MAX).min(at_millis);
                (start, end)
            })
            .filter(|(start, end)| end > start)
            .collect();
        intervals.sort_unstable();
        let mut covered = 0_u64;
        let mut merged: Option<(u64, u64)> = None;
        for (start, end) in intervals {
            match merged {
                Some((merged_start, merged_end)) if start <= merged_end => {
                    merged = Some((merged_start, merged_end.max(end)));
                }
                Some((merged_start, merged_end)) => {
                    covered = covered.saturating_add(merged_end - merged_start);
                    merged = Some((start, end));
                }
                None => merged = Some((start, end)),
            }
        }
        if let Some((merged_start, merged_end)) = merged {
            covered = covered.saturating_add(merged_end - merged_start);
        }
        covered
    }

    /// Returns every receipt that crossed its effective deadline at `now`.
    ///
    /// The effective deadline is the request time plus the copy class's
    /// charter deadline plus hold coverage clipped to the receipt's own
    /// completion time.
    #[must_use]
    pub fn deadline_violations_at(&self, now_millis: u64) -> Vec<DeadlineViolation> {
        self.receipts
            .values()
            .filter_map(|receipt| {
                let coverage = self.hold_coverage_millis(now_millis, receipt.completed_at_millis());
                let deadline = self
                    .requested_at_millis
                    .saturating_add(receipt.target().class().deadline_millis())
                    .saturating_add(coverage);
                (receipt.completed_at_millis() > deadline).then(|| DeadlineViolation {
                    target: receipt.target().clone(),
                    deadline_at_millis: deadline,
                    completed_at_millis: receipt.completed_at_millis(),
                })
            })
            .collect()
    }

    /// Encodes the workflow in its canonical, checksummed representation.
    #[must_use]
    pub fn encoded(&self) -> Vec<u8> {
        let body = self.canonical_body();
        let mut encoded = body.clone();
        encoded.extend_from_slice(&workflow_checksum(&body));
        encoded
    }

    /// Decodes and verifies one canonical workflow representation.
    ///
    /// Recovery uses this as the restart interface: decode a workflow's
    /// durable bytes and continue execution idempotently.
    pub fn decode(bytes: &[u8]) -> Result<Self, DeletionError> {
        if bytes.len() < MAGIC.len() + 2 + 2 + 32 {
            return Err(DeletionError::MalformedEncoding);
        }
        let (body, stored_bytes) = bytes.split_at(bytes.len() - CHECKSUM_LEN);
        let stored: [u8; 32] =
            stored_bytes.try_into().map_err(|_| DeletionError::MalformedEncoding)?;
        if workflow_checksum(body) != stored {
            return Err(DeletionError::ChecksumMismatch);
        }
        let mut reader = Reader::new(body);
        if reader.take(MAGIC.len())? != MAGIC || reader.u16()? != DELETION_FORMAT_VERSION {
            return Err(DeletionError::MalformedEncoding);
        }
        let state = decode_state(reader.u8()?, reader.u8()?)?;
        let request_id = DeletionRequestId::try_new(&reader.string()?)?;
        let tenant =
            TenantId::try_new(&reader.string()?).map_err(|_| DeletionError::InvalidIdentifier)?;
        let scope = DeletionScope::try_new(tenant, &reader.string()?)?;
        let verified_at_millis = reader.u64()?;
        let requested_at_millis = reader.u64()?;
        let selected_regions = reader.region_set()?;
        let admission = match reader.u8()? {
            0 => None,
            1 => {
                let acknowledged = reader.region_set()?;
                let admitted_at_millis = reader.u64()?;
                Some(GlobalDurableAdmission::new(acknowledged, admitted_at_millis))
            }
            _ => return Err(DeletionError::MalformedEncoding),
        };
        let plan = reader.target_set()?;
        let receipts = reader.receipts()?;
        let holds = reader.holds()?;
        let hold_ends = reader.hold_ends()?;
        let verified_at = reader.optional_time()?;
        let completed_at = reader.optional_time()?;
        if reader.remaining() != 0 {
            return Err(DeletionError::NonCanonicalEncoding);
        }
        let workflow = Self {
            request_id,
            scope,
            verified_at_millis,
            requested_at_millis,
            selected_regions,
            admission,
            plan,
            receipts,
            holds,
            hold_ends,
            state,
            verified_at,
            completed_at,
        };
        workflow.validate()?;
        if workflow.encoded().as_slice() != bytes {
            return Err(DeletionError::NonCanonicalEncoding);
        }
        Ok(workflow)
    }

    /// Re-checks every invariant after construction or decode.
    fn validate(&self) -> Result<(), DeletionError> {
        if self.selected_regions.is_empty() {
            return Err(DeletionError::EmptyRegionSet);
        }
        if self.verified_at_millis > self.requested_at_millis {
            return Err(DeletionError::InvalidVerification);
        }
        let Some(admission) = self.admission.as_ref() else {
            if self.state != DeletionState::Requested
                || !self.plan.is_empty()
                || !self.receipts.is_empty()
            {
                return Err(DeletionError::NotAdmitted);
            }
            return self.validate_tail();
        };
        if admission.admitted_at_millis() < self.requested_at_millis {
            return Err(DeletionError::InvalidAdmissionTime);
        }
        for region in admission.acknowledged_regions() {
            if !self.selected_regions.contains(region) {
                return Err(DeletionError::UnknownRegion);
            }
        }
        for region in &self.selected_regions {
            let acknowledged =
                admission.acknowledged_regions().any(|candidate| candidate == region);
            if !acknowledged {
                return Err(DeletionError::AdmissionNotGlobalDurable);
            }
        }
        if self.plan.is_empty() || self.plan.len() > MAX_PLAN_TARGETS {
            return Err(DeletionError::InvalidPlan);
        }
        for target in &self.plan {
            if target.region().is_some_and(|region| !self.selected_regions.contains(region)) {
                return Err(DeletionError::UnknownRegion);
            }
        }
        for region in &self.selected_regions {
            let covered = self.plan.iter().any(|target| {
                target.class() == CopyClass::LiveRegion && target.region() == Some(region)
            });
            if !covered {
                return Err(DeletionError::PlanMissesRegion);
            }
        }
        let admitted_at = admission.admitted_at_millis();
        for receipt in self.receipts.values() {
            if !self.plan.contains(receipt.target()) {
                return Err(DeletionError::CopyTargetNotPlanned);
            }
            if receipt.completed_at_millis() < admitted_at {
                return Err(DeletionError::InvalidReceiptTime);
            }
            let expected_outcome = match receipt.target().class() {
                CopyClass::BackupRotation => CopyOutcome::RotatedOut,
                _ => CopyOutcome::Removed,
            };
            if receipt.outcome() != expected_outcome {
                return Err(DeletionError::InvalidReceiptOutcome);
            }
        }
        if self.state == DeletionState::Admitted && !self.receipts.is_empty() {
            return Err(DeletionError::InvalidTransition);
        }
        self.validate_tail()
    }

    /// The invariants that hold with or without an admission.
    fn validate_tail(&self) -> Result<(), DeletionError> {
        for (authority, end) in &self.hold_ends {
            if !self.holds.contains_key(authority) {
                return Err(DeletionError::UnknownHold);
            }
            let hold = self.holds.get(authority).expect("checked");
            if *end > hold.expires_at_millis() {
                return Err(DeletionError::InvalidHoldWindow);
            }
            if *end < hold.recorded_at_millis() {
                return Err(DeletionError::InvalidHoldWindow);
            }
        }
        if self.holds.len() > MAX_HOLDS {
            return Err(DeletionError::HoldLimitExceeded);
        }
        let verified_coherent = self.verified_at.is_some()
            == matches!(self.state, DeletionState::Verified | DeletionState::Complete);
        let completed_coherent =
            self.completed_at.is_some() == (self.state == DeletionState::Complete);
        if !verified_coherent || !completed_coherent {
            return Err(DeletionError::InvalidTransition);
        }
        if matches!(self.state, DeletionState::Executing) && self.receipts.is_empty() {
            return Err(DeletionError::InvalidTransition);
        }
        Ok(())
    }

    fn canonical_body(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(256);
        bytes.extend_from_slice(MAGIC);
        bytes.extend_from_slice(&DELETION_FORMAT_VERSION.to_le_bytes());
        bytes.push(state_tag(self.state));
        if let DeletionState::Failed(cause) = self.state {
            bytes.push(failure_tag(cause));
        } else {
            bytes.push(0);
        }
        push_string(&mut bytes, self.request_id.as_str());
        push_string(&mut bytes, self.scope.tenant_id().as_str());
        push_string(&mut bytes, self.scope.subject_id().as_str());
        bytes.extend_from_slice(&self.verified_at_millis.to_le_bytes());
        bytes.extend_from_slice(&self.requested_at_millis.to_le_bytes());
        let selected: BTreeSet<&RegionId> = self.selected_regions.iter().collect();
        push_region_set(&mut bytes, &selected);
        match &self.admission {
            Some(admission) => {
                bytes.push(1);
                let acknowledged: BTreeSet<&RegionId> = admission.acknowledged_regions().collect();
                push_region_set(&mut bytes, &acknowledged);
                bytes.extend_from_slice(&admission.admitted_at_millis().to_le_bytes());
            }
            None => bytes.push(0),
        }
        push_target_set(&mut bytes, &self.plan);
        push_receipts(&mut bytes, &self.receipts);
        push_holds(&mut bytes, &self.holds);
        push_hold_ends(&mut bytes, &self.hold_ends);
        push_optional_time(&mut bytes, self.verified_at);
        push_optional_time(&mut bytes, self.completed_at);
        bytes
    }
}

/// An idempotent index of deletion workflows by request identity.
///
/// This is the domain shape a durable store keeps; it enforces the
/// charter's idempotent-workflow rule: the same request identity with the
/// same facts is one workflow, and the same identity with different facts
/// is a typed refusal.
#[derive(Clone, Debug, Default)]
pub struct DeletionLedger {
    workflows: BTreeMap<DeletionRequestId, DeletionWorkflow>,
}

/// The outcome of submitting one workflow to the ledger.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LedgerOutcome {
    /// The workflow was newly recorded.
    Inserted,
    /// The exact workflow was already present; no state changed.
    Duplicate,
}

impl DeletionLedger {
    /// Submits one recorded request, idempotently.
    ///
    /// The comparison is over canonical bytes, so a replayed request after
    /// restart is a `Duplicate` and never a second logical workflow.
    pub fn submit(&mut self, workflow: DeletionWorkflow) -> Result<LedgerOutcome, DeletionError> {
        if let Some(existing) = self.workflows.get(workflow.request_id()) {
            if existing.encoded() == workflow.encoded() {
                return Ok(LedgerOutcome::Duplicate);
            }
            return Err(DeletionError::WorkflowConflict);
        }
        self.workflows.insert(workflow.request_id().clone(), workflow);
        Ok(LedgerOutcome::Inserted)
    }

    /// Looks up one workflow by request identity.
    #[must_use]
    pub fn get(&self, request_id: &DeletionRequestId) -> Option<&DeletionWorkflow> {
        self.workflows.get(request_id)
    }

    /// Looks up one workflow mutably by request identity.
    pub fn get_mut(&mut self, request_id: &DeletionRequestId) -> Option<&mut DeletionWorkflow> {
        self.workflows.get_mut(request_id)
    }

    /// Returns every workflow in stable identity order.
    pub fn workflows(&self) -> impl Iterator<Item = &DeletionWorkflow> {
        self.workflows.values()
    }
}

const MAGIC: &[u8; 8] = b"TBDWF001";
const CHECKSUM_LEN: usize = 32;

fn state_tag(state: DeletionState) -> u8 {
    match state {
        DeletionState::Requested => 0,
        DeletionState::Admitted => 1,
        DeletionState::Executing => 2,
        DeletionState::Verified => 3,
        DeletionState::Complete => 4,
        DeletionState::Failed(_) => 5,
    }
}

fn decode_state(tag: u8, failure: u8) -> Result<DeletionState, DeletionError> {
    Ok(match tag {
        0 => DeletionState::Requested,
        1 => DeletionState::Admitted,
        2 => DeletionState::Executing,
        3 => DeletionState::Verified,
        4 => DeletionState::Complete,
        5 => match failure {
            1 => DeletionState::Failed(DeletionFailure::DeadlineExceeded),
            _ => return Err(DeletionError::MalformedEncoding),
        },
        _ => return Err(DeletionError::MalformedEncoding),
    })
}

fn failure_tag(cause: DeletionFailure) -> u8 {
    match cause {
        DeletionFailure::DeadlineExceeded => 1,
    }
}

fn class_tag(class: CopyClass) -> u8 {
    match class {
        CopyClass::LiveRegion => 0,
        CopyClass::Index => 1,
        CopyClass::Cache => 2,
        CopyClass::DerivedView => 3,
        CopyClass::BackupRotation => 4,
    }
}

fn class_from_tag(tag: u8) -> Result<CopyClass, DeletionError> {
    Ok(match tag {
        0 => CopyClass::LiveRegion,
        1 => CopyClass::Index,
        2 => CopyClass::Cache,
        3 => CopyClass::DerivedView,
        4 => CopyClass::BackupRotation,
        _ => return Err(DeletionError::MalformedEncoding),
    })
}

fn outcome_tag(outcome: CopyOutcome) -> u8 {
    match outcome {
        CopyOutcome::Removed => 0,
        CopyOutcome::RotatedOut => 1,
    }
}

fn outcome_from_tag(tag: u8) -> Result<CopyOutcome, DeletionError> {
    Ok(match tag {
        0 => CopyOutcome::Removed,
        1 => CopyOutcome::RotatedOut,
        _ => return Err(DeletionError::MalformedEncoding),
    })
}

fn hold_kind_tag(kind: HoldKind) -> u8 {
    match kind {
        HoldKind::Legal => 0,
        HoldKind::Security => 1,
        HoldKind::Payment => 2,
        HoldKind::Tax => 3,
        HoldKind::Fraud => 4,
    }
}

fn hold_kind_from_tag(tag: u8) -> Result<HoldKind, DeletionError> {
    Ok(match tag {
        0 => HoldKind::Legal,
        1 => HoldKind::Security,
        2 => HoldKind::Payment,
        3 => HoldKind::Tax,
        4 => HoldKind::Fraud,
        _ => return Err(DeletionError::MalformedEncoding),
    })
}

fn encode_target(bytes: &mut Vec<u8>, target: &CopyTarget) {
    bytes.push(class_tag(target.class()));
    match target.region() {
        Some(region) => {
            bytes.push(1);
            push_string(bytes, region.as_str());
        }
        None => bytes.push(0),
    }
    push_string(bytes, target.name().as_str());
}

fn decode_target(reader: &mut Reader<'_>) -> Result<CopyTarget, DeletionError> {
    let class = class_from_tag(reader.u8()?)?;
    let region = match reader.u8()? {
        0 => None,
        1 => Some(
            RegionId::try_new(&reader.string()?).map_err(|_| DeletionError::InvalidIdentifier)?,
        ),
        _ => return Err(DeletionError::MalformedEncoding),
    };
    let name = CopyName::try_new(&reader.string()?)?;
    if (class == CopyClass::LiveRegion) != region.is_some() {
        return Err(DeletionError::MalformedEncoding);
    }
    Ok(CopyTarget { class, region, name })
}

fn push_string(bytes: &mut Vec<u8>, value: &str) {
    let length = u16::try_from(value.len()).expect("validated deletion identifiers fit u16");
    bytes.extend_from_slice(&length.to_le_bytes());
    bytes.extend_from_slice(value.as_bytes());
}

fn push_count(bytes: &mut Vec<u8>, count: usize) {
    let length = u32::try_from(count).expect("bounded deletion collections fit u32");
    bytes.extend_from_slice(&length.to_le_bytes());
}

fn push_region_set(bytes: &mut Vec<u8>, regions: &BTreeSet<&RegionId>) {
    push_count(bytes, regions.len());
    for region in regions {
        push_string(bytes, region.as_str());
    }
}

fn push_target_set(bytes: &mut Vec<u8>, targets: &BTreeSet<CopyTarget>) {
    push_count(bytes, targets.len());
    for target in targets {
        encode_target(bytes, target);
    }
}

fn push_receipts(bytes: &mut Vec<u8>, receipts: &BTreeMap<CopyTarget, CopyReceipt>) {
    push_count(bytes, receipts.len());
    for receipt in receipts.values() {
        encode_target(bytes, receipt.target());
        bytes.push(outcome_tag(receipt.outcome()));
        bytes.extend_from_slice(&receipt.completed_at_millis().to_le_bytes());
    }
}

fn push_holds(bytes: &mut Vec<u8>, holds: &BTreeMap<HoldAuthorityId, HoldRecord>) {
    push_count(bytes, holds.len());
    for hold in holds.values() {
        bytes.push(hold_kind_tag(hold.kind()));
        push_string(bytes, hold.authority().as_str());
        bytes.extend_from_slice(&hold.recorded_at_millis().to_le_bytes());
        bytes.extend_from_slice(&hold.expires_at_millis().to_le_bytes());
        let fields: Vec<&RetainedField> = hold.retained_fields().collect();
        push_count(bytes, fields.len());
        for field in fields {
            push_string(bytes, field.as_str());
        }
    }
}

fn push_hold_ends(bytes: &mut Vec<u8>, hold_ends: &BTreeMap<HoldAuthorityId, u64>) {
    push_count(bytes, hold_ends.len());
    for (authority, end) in hold_ends {
        push_string(bytes, authority.as_str());
        bytes.extend_from_slice(&end.to_le_bytes());
    }
}

fn push_optional_time(bytes: &mut Vec<u8>, time: Option<u64>) {
    match time {
        Some(value) => {
            bytes.push(1);
            bytes.extend_from_slice(&value.to_le_bytes());
        }
        None => bytes.push(0),
    }
}

fn workflow_checksum(bytes: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(DELETION_CHECKSUM_DOMAIN);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> Reader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'a [u8], DeletionError> {
        let end = self.position.checked_add(length).ok_or(DeletionError::MalformedEncoding)?;
        let value = self.bytes.get(self.position..end).ok_or(DeletionError::MalformedEncoding)?;
        self.position = end;
        Ok(value)
    }

    fn remaining(&self) -> usize {
        self.bytes.len() - self.position
    }

    fn u8(&mut self) -> Result<u8, DeletionError> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, DeletionError> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().expect("length checked")))
    }

    fn u32(&mut self) -> Result<u32, DeletionError> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().expect("length checked")))
    }

    fn u64(&mut self) -> Result<u64, DeletionError> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().expect("length checked")))
    }

    fn string(&mut self) -> Result<String, DeletionError> {
        let length = usize::from(self.u16()?);
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| DeletionError::InvalidIdentifier)
    }

    fn region_set(&mut self) -> Result<BTreeSet<RegionId>, DeletionError> {
        let count = self.u32()? as usize;
        let mut regions = BTreeSet::new();
        for _ in 0..count {
            regions.insert(
                RegionId::try_new(&self.string()?).map_err(|_| DeletionError::InvalidIdentifier)?,
            );
        }
        Ok(regions)
    }

    fn target_set(&mut self) -> Result<BTreeSet<CopyTarget>, DeletionError> {
        let count = self.u32()? as usize;
        let mut targets = BTreeSet::new();
        for _ in 0..count {
            targets.insert(decode_target(self)?);
        }
        Ok(targets)
    }

    fn receipts(&mut self) -> Result<BTreeMap<CopyTarget, CopyReceipt>, DeletionError> {
        let count = self.u32()? as usize;
        let mut receipts = BTreeMap::new();
        for _ in 0..count {
            let target = decode_target(self)?;
            let outcome = outcome_from_tag(self.u8()?)?;
            let completed_at_millis = self.u64()?;
            receipts.insert(target.clone(), CopyReceipt::new(target, outcome, completed_at_millis));
        }
        Ok(receipts)
    }

    fn holds(&mut self) -> Result<BTreeMap<HoldAuthorityId, HoldRecord>, DeletionError> {
        let count = self.u32()? as usize;
        let mut holds = BTreeMap::new();
        for _ in 0..count {
            let kind = hold_kind_from_tag(self.u8()?)?;
            let authority_text = self.string()?;
            let recorded_at_millis = self.u64()?;
            let expires_at_millis = self.u64()?;
            let field_count = self.u32()? as usize;
            let mut fields = Vec::with_capacity(field_count);
            for _ in 0..field_count {
                fields.push(self.string()?);
            }
            let field_refs: Vec<&str> = fields.iter().map(String::as_str).collect();
            let record = HoldRecord::try_new(
                kind,
                &authority_text,
                recorded_at_millis,
                expires_at_millis,
                &field_refs,
            )?;
            holds.insert(record.authority().clone(), record);
        }
        Ok(holds)
    }

    fn hold_ends(&mut self) -> Result<BTreeMap<HoldAuthorityId, u64>, DeletionError> {
        let count = self.u32()? as usize;
        let mut hold_ends = BTreeMap::new();
        for _ in 0..count {
            let authority = HoldAuthorityId::try_new(&self.string()?)?;
            let end = self.u64()?;
            hold_ends.insert(authority, end);
        }
        Ok(hold_ends)
    }

    fn optional_time(&mut self) -> Result<Option<u64>, DeletionError> {
        match self.u8()? {
            0 => Ok(None),
            1 => Ok(Some(self.u64()?)),
            _ => Err(DeletionError::MalformedEncoding),
        }
    }
}

fn validate_identifier(value: &str) -> Result<(), DeletionError> {
    if value.is_empty()
        || value.len() > MAX_IDENTIFIER_BYTES
        || value != value.trim()
        || value.chars().any(char::is_control)
    {
        return Err(DeletionError::InvalidIdentifier);
    }
    Ok(())
}

#[cfg(test)]
mod tests;
