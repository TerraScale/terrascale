//! Automatic index planning and lifecycle contracts (ADR 0045 / D-31).
//!
//! Workload algorithms and durable lifecycle helpers support the engine
//! coordinator in [`crate::auto_index`]. Storage owns index publication and
//! journal commits; optimizer recommendations do not publish indexes.

#![allow(dead_code, reason = "staged auto-index contracts are wired incrementally")]
#![allow(clippy::all, reason = "staged auto-index contracts are linted when integrated")]
#![allow(clippy::pedantic, reason = "staged auto-index contracts are linted when integrated")]

mod legacy;

pub use legacy::*;

pub(crate) mod bandit;
pub(crate) mod build;
pub(crate) mod candidates;
pub(crate) mod lifecycle;
pub(crate) mod monitor;
pub(crate) mod observe;
pub(crate) mod persistence;
pub(crate) mod shadow;

/// The durable lifecycle phase of an automatically managed index.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum AutoIndexPhase {
    /// The workload ring is collecting evidence.
    Observing,
    /// The workload has been compressed into weighted templates.
    Compressed,
    /// A candidate passed the hypothetical planner check.
    Selected,
    /// SQLite is building the index invisibly in sequence order.
    Building,
    /// The index is being measured against a shadow plan.
    Shadowing,
    /// The index is serving production plans.
    Active,
    /// The index is hidden pending delayed physical removal.
    Invisible,
    /// The index was rejected or physically removed.
    Abandoned,
}
