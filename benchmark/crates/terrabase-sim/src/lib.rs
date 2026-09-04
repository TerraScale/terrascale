//! Deterministic simulation harness skeleton for replication + sync (E-12).
//!
//! Qualifies the two engines of ADR 0044 against the eight invariants of
//! ADR 0044 §4 under seeded faults. The invariant qualification runs through
//! the durable [`production::ProductionWorld`] adapter and the trait seams
//! ([`traits::ReplicationEngine`], [`traits::SyncEngine`]); the in-memory
//! [`world::StubWorld`] remains a small reference model for unit tests.
//!
//! The modules, in dependency order:
//!
//! - [`rng`]: seeded RNG (`ChaCha8`) plus seed-batch helpers.
//! - [`model`]: world vocabulary shared by every adapter: regions, journal
//!   rows, frontier vectors, session tokens, view cuts.
//! - [`traits`]: the engine seams the real engines must implement.
//! - [`faults`]: fault injectors — partition, kill, clock skew, slow link,
//!   duplicate and reordered delivery — driven by a deterministic [`faults::FaultPlan`].
//! - [`workload`]: deterministic client-operation generator.
//! - [`world`]: in-memory reference engine for harness mechanics.
//! - [`check`]: the eight-invariant checklist and the seed-batch runner.

pub mod check;
pub mod faults;
pub mod model;
pub mod production;
pub mod rng;
pub mod traits;
pub mod workload;
pub mod world;

pub use check::{Invariant, SimConfig, SimReport, run_scenario};
pub use faults::{Fault, FaultKind, FaultPlan};
pub use model::{Frontier, Hlc, JournalRow, RegionId, SessionToken, ViewCut};
pub use production::{ProductionWorld, ProductionWorldError};
pub use rng::{SimRng, seed_batch};
pub use traits::{ReconcileStats, ReplicationEngine, SyncEngine, ViewRevision};
pub use workload::{Op, Workload};
pub use world::{StubWorld, VIEW_ALL, VIEW_EVEN_KEYS, capability_for};
