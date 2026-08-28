//! Typed lifecycle audit records for the Platform adapter seam.
//!
//! The audit skeleton records one typed lifecycle event per observable
//! dispatch transition — refused before the Engine, dispatched to the
//! Engine, and completed by the Engine — into a bounded in-process journal.
//! An event carries only structural identity: the lifecycle stage, the
//! closed operation kind, the non-secret principal, the tenant, and the
//! optional stable operation identity. No document body, document field,
//! credential, secret, or redacted fingerprint is ever recorded, and
//! recording never blocks a dispatch: the journal is bounded and in-process,
//! and durable audit shipping belongs to the later Platform side.
//!
//! This is the W5-E skeleton, not a durable audit claim: the journal lives
//! only as long as the adapter that owns it.

use std::{
    fmt,
    sync::{
        Mutex,
        atomic::{AtomicU64, Ordering},
    },
};

use crate::{
    adapters::protocol::PrincipalId,
    types::{OperationId, TenantId},
};

/// The maximum number of lifecycle events the in-process journal retains.
///
/// The bound keeps recording bounded and non-blocking: once the journal is
/// full, the oldest event is dropped in favor of the newest, and the
/// monotonic sequence keeps the truncation observable instead of hidden.
pub const MAX_AUDIT_EVENTS: usize = 1024;

/// The closed set of audited public operation kinds.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum AuditOperation {
    /// One document read.
    DocumentRead,
    /// One document write.
    DocumentWrite,
    /// One query execution.
    Query,
    /// One family read.
    FamilyRead,
    /// One family write.
    FamilyWrite,
    /// One schema declaration (declare, replace, or clear).
    SchemaDeclare,
    /// One schema status read.
    SchemaStatus,
    /// One on-demand document validation.
    SchemaValidate,
    /// One Schema Migration status read.
    SchemaMigrationStatus,
    /// One Schema Migration dry run.
    SchemaMigrationDryRun,
    /// One Schema Migration apply.
    SchemaMigrationApply,
}

impl fmt::Display for AuditOperation {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::DocumentRead => "document-read",
            Self::DocumentWrite => "document-write",
            Self::Query => "query",
            Self::FamilyRead => "family-read",
            Self::FamilyWrite => "family-write",
            Self::SchemaDeclare => "schema-declare",
            Self::SchemaStatus => "schema-status",
            Self::SchemaValidate => "schema-validate",
            Self::SchemaMigrationStatus => "schema-migration-status",
            Self::SchemaMigrationDryRun => "schema-migration-dry-run",
            Self::SchemaMigrationApply => "schema-migration-apply",
        })
    }
}

/// The closed refusal categories the audit skeleton records.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum AuditRefusal {
    /// Tenant isolation refused the dispatch.
    TenantDenied,
    /// The operation deadline had elapsed.
    Deadline,
    /// The operation was cancelled.
    Cancelled,
    /// The operation was malformed.
    InvalidRequest,
    /// The operation conflicted.
    Conflict,
    /// The Engine returned a typed failure.
    EngineFailure,
    /// A Platform-side denial category reserved for the future Platform
    /// authorization surface. Unreachable at this seam today: this local,
    /// trusted boundary performs no caller, permission, expiry,
    /// confirmation, audit-sink, or secret-policy denial.
    Denied,
}

impl fmt::Display for AuditRefusal {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::TenantDenied => "tenant-denied",
            Self::Deadline => "deadline",
            Self::Cancelled => "cancelled",
            Self::InvalidRequest => "invalid-request",
            Self::Conflict => "conflict",
            Self::EngineFailure => "engine-failure",
            Self::Denied => "denied",
        })
    }
}

/// The lifecycle stage of one audited dispatch.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuditStage {
    /// The operation passed tenant scoping and was dispatched to the Engine.
    Dispatched,
    /// The Engine answered the dispatched operation with a typed response.
    Completed,
    /// The dispatch was refused with the typed refusal category.
    Refused(AuditRefusal),
}

impl fmt::Display for AuditStage {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Dispatched => formatter.write_str("dispatched"),
            Self::Completed => formatter.write_str("completed"),
            Self::Refused(reason) => write!(formatter, "refused:{reason}"),
        }
    }
}

/// One typed, content-free lifecycle audit event.
///
/// The event names only structural identity. It structurally cannot carry a
/// document body, a document field, a credential, a secret, or a redacted
/// fingerprint: no field accepts one.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AuditEvent {
    sequence: u64,
    stage: AuditStage,
    operation: AuditOperation,
    principal: PrincipalId,
    tenant: TenantId,
    operation_id: Option<OperationId>,
}

impl AuditEvent {
    /// Creates one event from its structural identity parts.
    fn new(
        sequence: u64,
        stage: AuditStage,
        operation: AuditOperation,
        principal: PrincipalId,
        tenant: TenantId,
        operation_id: Option<OperationId>,
    ) -> Self {
        Self { sequence, stage, operation, principal, tenant, operation_id }
    }

    /// Returns the monotonic journal sequence of this event.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Returns the lifecycle stage of the audited dispatch.
    #[must_use]
    pub fn stage(&self) -> AuditStage {
        self.stage
    }

    /// Returns the audited operation kind.
    #[must_use]
    pub fn operation(&self) -> AuditOperation {
        self.operation
    }

    /// Returns the non-secret principal identifier of the dispatch.
    #[must_use]
    pub fn principal(&self) -> &PrincipalId {
        &self.principal
    }

    /// Returns the backend-supplied tenant of the dispatch.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the stable operation identity when the dispatch carried one.
    #[must_use]
    pub fn operation_id(&self) -> Option<&OperationId> {
        self.operation_id.as_ref()
    }
}

impl fmt::Display for AuditEvent {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "audit sequence={} stage={} operation={} principal={} tenant={}",
            self.sequence, self.stage, self.operation, self.principal, self.tenant
        )?;
        if let Some(operation_id) = &self.operation_id {
            write!(formatter, " operation_id={operation_id}")?;
        }
        Ok(())
    }
}

/// The bounded in-process journal of typed lifecycle events.
pub(crate) struct AuditJournal {
    next_sequence: AtomicU64,
    events: Mutex<Vec<AuditEvent>>,
}

impl AuditJournal {
    /// Creates an empty journal.
    pub(crate) fn new() -> Self {
        Self { next_sequence: AtomicU64::new(1), events: Mutex::new(Vec::new()) }
    }

    /// Records one lifecycle event, dropping the oldest beyond the bound.
    ///
    /// Recording takes the journal lock only: no Engine lock, no storage
    /// lock, and no I/O, so a dispatch is never blocked by audit recording.
    pub(crate) fn record(
        &self,
        stage: AuditStage,
        operation: AuditOperation,
        principal: PrincipalId,
        tenant: TenantId,
        operation_id: Option<OperationId>,
    ) {
        let sequence = self.next_sequence.fetch_add(1, Ordering::Relaxed);
        let event = AuditEvent::new(sequence, stage, operation, principal, tenant, operation_id);
        if let Ok(mut events) = self.events.lock() {
            events.push(event);
            let excess = events.len().saturating_sub(MAX_AUDIT_EVENTS);
            if excess > 0 {
                events.drain(0..excess);
            }
        }
        // A poisoned journal lock drops the event rather than panicking the
        // dispatch path; the sequence keeps advancing so the gap is visible.
    }

    /// Returns a snapshot of the retained events in recorded order.
    pub(crate) fn events(&self) -> Vec<AuditEvent> {
        self.events.lock().map(|events| events.clone()).unwrap_or_default()
    }
}
