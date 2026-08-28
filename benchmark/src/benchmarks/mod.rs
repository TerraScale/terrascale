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
//! - the **pinned reference identities** — the TerraBase V1 source pin and
//!   the decided PostgreSQL pin with its full server and client
//!   configuration — plus the honest status of a reference that did not run
//!   ([`reference`]);
//! - the **workload mapping as data**: each comparable workload's
//!   TerraBaseV2 operation and PostgreSQL equivalent, so the mapping
//!   document is generated from the same source the driver uses
//!   ([`lane::Workload`]) — including the sync-path workload class
//!   (PD-2026-08-26-05 D4, PD-2026-08-26-13 D1:
//!   [`lane::Workload::SYNC_PATH`]), whose four workloads are declared
//!   open slots with named owners rather than silently absent;
//! - the **V2-side driver** that really runs the engine workloads and
//!   returns exact, deterministic counters ([`driver`]);
//! - the **receipt renderer** that emits the canonical objective-status
//!   line every performance receipt must carry ([`record`]).
//!
//! ## What this adapter does not do
//!
//! It holds no PostgreSQL client and no V1 server: the engine workspace
//! adds no external dependency, so the reference *sides* of a lane run are
//! driven by the operator from the pinned identities in [`reference`]. A
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
//! `inconclusive`.

#![allow(clippy::doc_markdown)] // dense with the proper nouns PostgreSQL and TerraBaseV2

mod driver;
mod gates;
mod lane;
mod optimizer;
mod record;
mod reference;
mod vocabulary;

#[cfg(test)]
mod optimizer_tests;

#[cfg(test)]
mod tests;
