//! Backup scheduling and destination policy (BK-04): periodic scheduling
//! and status as typed operations, destination configuration, the
//! encryption and compression artifact codec, and retention enforcement
//! (PB-6011..PB-6014, engine side; PB-6017's admission and read bounds
//! live in the run seam this module drives).
//!
//! # The typed-operation shape
//!
//! Every capability here is a typed value operation an agent can drive
//! through the backend (PD-2026-08-26-11 D2): build a
//! [`BackupSchedule`], construct a [`ScheduleDriver`] over a validated
//! [`DestinationConfig`], and drive [`ScheduleDriver::run_due`] with a
//! caller-supplied tick — the engine runs **no timers**, because
//! Platform-owned task controls disposition to the Platform. All cadence
//! facts live in the caller's tick domain; retention ages live in the
//! destination's wall-clock domain and are injected per sweep, so nothing
//! in this module reads a hidden clock for a verdict.
//!
//! The caller owns scheduler state durability: [`ScheduleState`] is a
//! value the backend stores between driven ticks and hands back to every
//! operation. The engine keeps no scheduler state of its own.
//!
//! # What a driven tick does
//!
//! [`ScheduleDriver::run_due`] decides at most one run: the first full of
//! a schedule, an incremental with new journal since the newest sealed
//! point (chained on that point's boundary until the full interval
//! rotates the chain), or a recorded no-op when the source is quiet. It
//! probes the destination, executes through the public
//! [`crate::backup::run_backup`] seam, seals the scheduled artifact
//! bundle (compressed, and encrypted with a per-operation key when the
//! schedule says so), and enforces retention — each step's failure typed
//! and recorded in state and status, never silently swallowed.
//!
//! # Secrets
//!
//! Key material is supplied per operation, never stored by the engine,
//! never written into a bundle, status, or receipt, and zeroed on drop.
//! Schedule status carries phases, boundaries, identities, counts, and
//! typed failures — never a destination path or archive content.
//!
//! # Limits and dispositions
//!
//! Focused tests only: formal Recovery Receipts and scheduled-chain lane
//! fixtures belong to the exclusive recovery lane (BK-02). The bundle is
//! an authenticated artifact derived from a sealed recovery point; at-rest
//! encryption of the plaintext point directories themselves is archive-
//! writer territory (BK-01's files) and stays open. Remote destination
//! kinds (object stores, and so on) are future typed variants of
//! [`DestinationKind`], each with its own validation and qualification.
//!
//! This module is private to the crate until the integrator exposes the
//! typed surface at `crate::backup` (recorded collision action: a
//! one-line `pub use` in `src/backup/mod.rs`); until then the
//! backend-drivable surface is exercised by the in-crate focused suite.

#![allow(dead_code)]

mod codec;
mod destination;
mod retention;
mod scheduler;

#[cfg(test)]
mod tests;

// The re-export list is the module's typed-operation surface — the exact
// names the backend drives once the integrator exposes this module from
// `crate::backup`. Items no in-crate consumer references yet (run outcomes,
// status, receipts) are part of that surface, not dead code.
#[allow(unused_imports)]
pub use codec::{
    ArtifactBundle, BUNDLE_COMPRESSION_ALGORITHM, BUNDLE_ENCRYPTION_ALGORITHM, BundleOptions,
    CompressionError, EncryptionError, EncryptionKey, compress, decompress,
    extract_artifact_bundle, open_artifact_bundle, seal_artifact_bundle,
};
#[allow(unused_imports)]
pub use destination::{DestinationConfig, DestinationError, DestinationInvalid, DestinationKind};
#[allow(unused_imports)]
pub use retention::{
    PrunedPoint, RetentionError, RetentionFailure, RetentionPolicy, RetentionReceipt,
    enforce_retention,
};
#[allow(unused_imports)]
pub use scheduler::{
    BackupSchedule, CompletedRun, MissedRunPolicy, RetentionSummary, ScheduleDecision,
    ScheduleDriver, ScheduleError, ScheduleFailure, ScheduleInvalid, SchedulePhase,
    ScheduleRunError, ScheduleRunOutcome, ScheduleState, ScheduleStatus, ScheduleTick,
};
