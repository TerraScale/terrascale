//! SE-02 spike: Loro as the engine server's server-authoritative sync core.
//!
//! This crate is the go/no-go prototype for ticket SE-02. It proves the
//! server-authoritative mapping end-to-end inside the engine server's
//! delivery layer: committed journal events are materialized into a Loro
//! document by the single server writer; a consumer prototype receives
//! byte-budgeted delta batches, resumes from a recorded checkpoint after
//! reconnect, and observes bounded assembly memory under a commit burst;
//! replays are deterministic.
//!
//! Scope limits, stated exactly:
//!
//! - This is a spike, not the seam. The seam crate `terrabase-sync-core`
//!   (accepted by the SE-02 decision record) carries the typed
//!   frame/checkpoint contract; this prototype produced its evidence.
//! - Clients never multi-master documents. The only writer is the engine
//!   server materializer at a fixed peer id; the consumer prototype only
//!   imports server-produced bytes (PD-2026-08-26-05 D1/D2, the wave spec's
//!   hybrid-adoption assumption).
//! - No performance claim is made or implied by anything in this crate.
//!   Measurements here are bytes-on-wire observations under
//!   PD-2026-08-26-13 D1 (the seed of SE-07's budgets), not benchmark
//!   receipts under PD-2026-08-26-03 (those require the exclusive
//!   benchmark lane and the TerraBase.Benchmark gates).
//! - All data is synthetic. No tenant, path, credential, or document body
//!   from any real system appears in code, tests, or output.

pub mod consumer;
pub mod journal;
pub mod mapping;
pub mod measure;
pub mod scenario;

/// The fixed Loro peer id of the engine server's materializer.
///
/// Server-authoritative means exactly one writer per sync scope, so the
/// peer id is a constant, which is also what makes replay deterministic
/// (see [`scenario::deterministic_replay`]).
pub const SERVER_PEER: u64 = 1;

/// The peer id the consumer prototype imports under. It never writes, so
/// its id never enters an op; asserting it differs from
/// [`SERVER_PEER`] keeps that invariant visible in tests.
pub const CONSUMER_PEER: u64 = 2;
