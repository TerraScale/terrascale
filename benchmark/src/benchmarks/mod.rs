//! The SE-15 benchmark-lane adapter: the methodology half of the
//! PostgreSQL-parity objective (PD-2026-08-26-03 D2/D3).
//!
//! This module is the engine-side half of the exclusive benchmark lane. It
//! owns, as test-only adapter code exactly like the SE-07 efficiency
//! adapters:
//!
//! - the charter's **exact claim vocabulary** — a comparison verdict
//!   (`v2-faster`, `no-improvement`, `v2-regression`, `inconclusive`) and
//!   the separate objective status (`met`, `not met`, `inconclusive`) — as
//!   closed enums, so no receipt can invent a word;
//! - the **statistical gate carried over from V1** unchanged: a change is
//!   called only when the median moves by more than the noise percent *and*
//!   every sample of one side separates from every sample of the other;
//!   overlapping ranges are stable noise ([`gates`]);
//! - the **lane configuration** and V1's recorded-run minimums, the
//!   warm-up discipline, and the order alternation that cancels
//!   first-run/second-run bias ([`lane`]);
//! - the **pinned PostgreSQL reference identity** — the decided PostgreSQL
//!   pin with its full server and client configuration — plus the honest
//!   status of a reference that did not run ([`reference`]);
//! - the **workload mapping as data**: each comparable workload's
//!   TerraBase operation and PostgreSQL equivalent, so the mapping
//!   document is generated from the same source the driver uses
//!   ([`lane::Workload`]) — including the sync-path workload class
//!   (PD-2026-08-26-05 D4, PD-2026-08-26-13 D1:
//!   [`lane::Workload::SYNC_PATH`]), whose four workloads are declared
//!   open slots with named owners rather than silently absent;
//! - the **TerraBase-side driver** that really runs the engine workloads and
//!   returns exact, deterministic counters ([`driver`]);
//! - the **receipt renderer** that emits the canonical objective-status
//!   line every performance receipt must carry ([`record`]);
//! - the **optimizer measurement class** ([`optimizer`], IX-04): the
//!   auto-indexed ad-hoc workloads of PD-2026-08-26-09 D3 — selection
//!   quality, auto-index creation cost, write-path overhead, and the
//!   bounded registry — with their budgets written before any measurement
//!   ran and decided by the budget tests in [`optimizer_tests`].
//!
//! ## What this adapter does not do
//!
//! It holds no PostgreSQL client or external reference server: the engine
//! workspace adds no external dependency, so the operator drives the reference
//! side of a lane run from the pinned identity in [`reference`]. A
//! reference that did not execute is recorded as `Unrun` with the concrete
//! environment gap and a named owner — never as a number, never as a silent
//! skip, and never as a hidden weakening of the gate.
//!
//! For the same reason it does not drive the sync-path workload class: that
//! plane lives in the sibling crates (`terrabase-sync-core`,
//! `terrabase-member-transport`, the TBEP host) the zero-dependency engine
//! crate cannot call, and the wall-clock quantities additionally need both
//! reference baselines to have run. The class is therefore declared as
//! open slots in [`lane::Workload::driver_status`] with a named owner, the
//! same pattern the `session` workload uses — never skipped silently.
//!
//! The fail-closed rule is structural: [`gates::reference_comparison_status`]
//! returns `inconclusive` for *any* comparison while either reference is
//! `Unrun`, so a wall-clock claim cannot be produced by forgetting the gap.
//!
//! ## Where the wall clock is admissible
//!
//! Nowhere in a verdict. The deterministic counters here (operations,
//! durable bytes appended, documents returned) are reproducible facts about
//! an operation stream, in the SE-07 lane's sense. Wall-clock samples are
//! taken only by the commissioning sampler to prove the gate machinery, and
//! they are rendered with the status and verdict both forced to
//! `inconclusive`. The IX-04 class takes no wall-clock sample at all: with
//! no reference baseline on this host a timing would be engineering
//! evidence supporting nothing, so its receipts carry counters only — plus
//! the one comparison that does exist (write-path durable overhead with and
//! without active auto indexes), which the SE-15 gate reads and whose
//! objective status is still fail-closed to `inconclusive`.

#![allow(clippy::doc_markdown)] // dense with the proper nouns PostgreSQL and TerraBase

mod driver;
mod gates;
mod lane;
#[cfg(any(test, feature = "bench-tools"))]
mod lane_run;
mod optimizer;
mod record;
mod reference;
mod vocabulary;

/// Compatibility entry point for the retired served benchmark binary.
///
/// The old Go gateway/qualification lifecycle was removed. Direct public
/// boundary families own their adapters under `bench/`; this function remains
/// only so an old binary fails closed instead of launching deleted code.
#[doc(hidden)]
pub fn run_served_boundary_cli(workload: &str) -> Result<std::path::PathBuf, String> {
    Err(format!(
        "served benchmark workload {workload:?} was retired with the obsolete Go gateway; use a direct public-boundary family adapter"
    ))
}

#[cfg(test)]
mod optimizer_tests;

#[cfg(test)]
mod tests;
