//! Typed replication observation operations (GAP-R03, PB-5035).
//!
//! This module owns how a member's bounded observable facts — the conflict
//! history, the fenced-leader history, the depositions, the tenant-isolation
//! denial log, the journal-derived cursor, and the health derivation — are
//! acquired as typed operations. The decision record is
//! `docs/replication/gapr03-replication-observations.md`; this documentation
//! states the rules, the record states why they are these rules.
//!
//! Three rules govern every operation in the module:
//!
//! - **Read-only.** An observation copies a bounded set of facts; it never
//!   transitions replication state, which is why every operation takes the
//!   member by shared reference. The watermark's transitions stay the
//!   explicit typed operations they already are.
//! - **Scope-gated.** Every operation runs under a caller-supplied
//!   [`TenantScope`] and an untrusted tenant claim, and the first act of
//!   each is the same gate: a claim the scope does not name is refused with
//!   the typed [`ObservationRefusal::CrossTenant`] before any fact is read
//!   — the engine's `TenantScope` discipline mirrored onto the observation
//!   seam, with the data plane's own refusal (`MemberError::CrossTenant`)
//!   as the template. The gate is uniform across all six operations,
//!   including the member-plane facts that carry no per-tenant data,
//!   because a discipline without exceptions is the one that audits clean.
//! - **Content-free by construction.** No observation type has a field,
//!   accessor, or rendering path that can carry a document body, a field
//!   value, or a collection's payload: identities, topology text, epochs,
//!   sequences, clocks, counts, and verdicts only. V1's conflict
//!   observation carried the conflicting documents themselves; V2's does
//!   not (an accepted difference the decision record states).
//!
//! ```compile_fail
//! // No document body can leave the observation surface: a conflicts
//! // observation has no such accessor, so the content restriction is a
//! // construction property, not a filter.
//! let observation: terrabase::replication::ConflictsObservation =
//!     unimplemented!();
//! let _ = observation.documents();
//! ```
//!
//! ```compile_fail
//! // The cursor observation reports positions in the storage-sequence
//! // space, never the documents the positions describe.
//! let observation: terrabase::replication::CursorObservation =
//!     unimplemented!();
//! let _ = observation.documents();
//! ```
//!
//! # Limits
//!
//! The bounded logs this module observes are in-memory: a restarted member
//! observes an empty conflict, fencing, deposition, and denial history
//! until the data plane records new facts (the cursor is journal-derived
//! and survives). The TBEP registry carries the wire-side slots for these
//! operations; serving them from a live engine process is the engine-backed
//! host integrator action the decision record names.

use super::member::{Deposition, FencedBatch, ReplicationMember, TenantDenial};
use super::{ConflictRecord, MemberId, ReplicationStatus};
use crate::types::{StorageSequence, TenantId, TenantScope};

/// The typed refusal of one observation operation.
///
/// Every refusal is fail-closed: no fact is read past the gate that raised
/// it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ObservationRefusal {
    /// The request named a tenant the caller's scope does not, so no
    /// observation was made. Mirrors the data plane's
    /// `MemberError::CrossTenant` and the engine's `TenantScope`
    /// discipline: the claim is untrusted input, the scope is the
    /// authorized truth.
    CrossTenant {
        /// The tenant the caller's scope named.
        scope: TenantId,
        /// The tenant the request claimed.
        attempted: TenantId,
    },
    /// The member's engine is closed, so the journal-derived cursor cannot
    /// be derived. A fact, never a guess: the last durable cursor stays
    /// what it was, unobserved.
    EngineClosed,
    /// The member's journal could not be read to derive the cursor, so no
    /// position is reported.
    JournalUnreadable,
}

/// The observation of one member's bounded conflict history for one tenant
/// (PB-5022).
///
/// Carries only the conflicts recorded under the claimed tenant's keys, in
/// the member's recorded order, with the recorded policy outcomes. No
/// conflicting document payload crosses this type.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ConflictsObservation {
    member: MemberId,
    tenant: TenantId,
    records: Vec<ConflictRecord>,
}

impl ConflictsObservation {
    /// Returns the observed member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the tenant whose conflicts were observed.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the observed conflicts, in recorded order.
    #[must_use]
    pub fn records(&self) -> &[ConflictRecord] {
        &self.records
    }
}

/// The observation of one member's fenced-leader history (D-1 fencing).
///
/// Names the below-epoch batches the member fenced: the range, the epoch
/// the batch carried, the highest epoch the member had seen, and the
/// deposed source that pushed it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FencedHistoryObservation {
    member: MemberId,
    records: Vec<FencedBatch>,
}

impl FencedHistoryObservation {
    /// Returns the observed member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the observed fenced batches, in recorded order.
    #[must_use]
    pub fn records(&self) -> &[FencedBatch] {
        &self.records
    }
}

/// The observation of one member's depositions.
///
/// Names the ownership transitions that deposed this member: the range, the
/// epoch the ownership was held at, the epoch the new owner holds, and the
/// new owner.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DepositionsObservation {
    member: MemberId,
    records: Vec<Deposition>,
}

impl DepositionsObservation {
    /// Returns the observed member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the observed depositions, in recorded order.
    #[must_use]
    pub fn records(&self) -> &[Deposition] {
        &self.records
    }
}

/// The observation of one member's tenant-isolation denial log for one
/// tenant's scope.
///
/// Carries only the denials recorded against the claimed tenant's scope —
/// the isolation evidence of attempts to reach another tenant's data
/// through this member — naming the seam, the attempted tenant, and the
/// counterparty.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DenialsObservation {
    member: MemberId,
    tenant: TenantId,
    records: Vec<TenantDenial>,
}

impl DenialsObservation {
    /// Returns the observed member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the tenant whose scope's denials were observed.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the observed denials, in recorded order.
    #[must_use]
    pub fn records(&self) -> &[TenantDenial] {
        &self.records
    }
}

/// The observation of one tenant's replication cursor position on one
/// member (PB-5031, PB-5033).
///
/// The position is the tenant's latest journaled sequence on the member's
/// own publication-history journal — a durable fact derived from the
/// journal, expressed in the member's storage-sequence space. A tenant with
/// no commits observes `None`: a fact, not an error.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CursorObservation {
    member: MemberId,
    tenant: TenantId,
    position: Option<StorageSequence>,
}

impl CursorObservation {
    /// Returns the observed member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the tenant whose position was observed.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the tenant's latest journaled sequence on the member's
    /// cursor, or `None` when the tenant has no commits.
    #[must_use]
    pub const fn position(&self) -> Option<StorageSequence> {
        self.position
    }
}

/// The observation of one member's health (PB-5032).
///
/// Health is reported separately from convergence state: the observation
/// never carries or implies a [`Convergence`](super::Convergence) fact.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HealthObservation {
    member: MemberId,
    status: ReplicationStatus,
}

impl HealthObservation {
    /// Returns the observed member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the observed health status.
    #[must_use]
    pub const fn status(&self) -> ReplicationStatus {
        self.status
    }
}

/// The one gate every observation operation passes first.
///
/// A claim the scope does not name is refused typed before any fact is
/// read, exactly as the data-plane seams refuse a cross-tenant catch-up.
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
fn refuse_cross_tenant(
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<(), ObservationRefusal> {
    if scope.tenant() != tenant_claim {
        return Err(ObservationRefusal::CrossTenant {
            scope: scope.tenant().clone(),
            attempted: tenant_claim.clone(),
        });
    }
    Ok(())
}

/// Observes one member's bounded conflict history for the claimed tenant.
///
/// # Errors
///
/// Returns [`ObservationRefusal::CrossTenant`] when the claim names a
/// tenant the scope does not.
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
pub(crate) fn observe_conflicts(
    member: &ReplicationMember,
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<ConflictsObservation, ObservationRefusal> {
    refuse_cross_tenant(scope, tenant_claim)?;
    let records = member
        .conflicts()
        .iter()
        .filter(|record| record.tenant() == tenant_claim)
        .cloned()
        .collect();
    Ok(ConflictsObservation { member: member.id().clone(), tenant: tenant_claim.clone(), records })
}

/// Observes one member's fenced-leader history.
///
/// # Errors
///
/// Returns [`ObservationRefusal::CrossTenant`] when the claim names a
/// tenant the scope does not.
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
pub(crate) fn observe_fenced_history(
    member: &ReplicationMember,
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<FencedHistoryObservation, ObservationRefusal> {
    refuse_cross_tenant(scope, tenant_claim)?;
    Ok(FencedHistoryObservation { member: member.id().clone(), records: member.fenced().to_vec() })
}

/// Observes one member's depositions.
///
/// # Errors
///
/// Returns [`ObservationRefusal::CrossTenant`] when the claim names a
/// tenant the scope does not.
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
pub(crate) fn observe_depositions(
    member: &ReplicationMember,
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<DepositionsObservation, ObservationRefusal> {
    refuse_cross_tenant(scope, tenant_claim)?;
    Ok(DepositionsObservation {
        member: member.id().clone(),
        records: member.depositions().to_vec(),
    })
}

/// Observes one member's tenant-isolation denial log for the claimed
/// tenant's scope.
///
/// # Errors
///
/// Returns [`ObservationRefusal::CrossTenant`] when the claim names a
/// tenant the scope does not.
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
pub(crate) fn observe_denials(
    member: &ReplicationMember,
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<DenialsObservation, ObservationRefusal> {
    refuse_cross_tenant(scope, tenant_claim)?;
    let records = member
        .denials()
        .iter()
        .filter(|denial| denial.scope_tenant() == tenant_claim)
        .cloned()
        .collect();
    Ok(DenialsObservation { member: member.id().clone(), tenant: tenant_claim.clone(), records })
}

/// Observes the claimed tenant's replication cursor position on one member.
///
/// # Errors
///
/// Returns [`ObservationRefusal::CrossTenant`] when the claim names a
/// tenant the scope does not, [`ObservationRefusal::EngineClosed`] when the
/// member's engine is closed, and
/// [`ObservationRefusal::JournalUnreadable`] when the journal cannot be
/// read to derive the cursor.
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
pub(crate) fn observe_cursor(
    member: &ReplicationMember,
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<CursorObservation, ObservationRefusal> {
    refuse_cross_tenant(scope, tenant_claim)?;
    let cursor = member.cursor().map_err(|error| match error {
        super::member::MemberError::EngineClosed => ObservationRefusal::EngineClosed,
        _ => ObservationRefusal::JournalUnreadable,
    })?;
    Ok(CursorObservation {
        member: member.id().clone(),
        tenant: tenant_claim.clone(),
        position: cursor.tenant_progress(tenant_claim),
    })
}

/// Observes one member's health.
///
/// # Errors
///
/// Returns [`ObservationRefusal::CrossTenant`] when the claim names a
/// tenant the scope does not. Health itself never fails: a closed member's
/// observation reports [`ReplicationStatus::Unavailable`].
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R03 observation tests; the TBEP host mapping is the recorded integrator action"
)]
pub(crate) fn observe_health(
    member: &ReplicationMember,
    scope: &TenantScope,
    tenant_claim: &TenantId,
) -> Result<HealthObservation, ObservationRefusal> {
    refuse_cross_tenant(scope, tenant_claim)?;
    Ok(HealthObservation { member: member.id().clone(), status: member.health() })
}
