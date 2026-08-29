//! Deterministic, bounded native CRDT datatypes.
//!
//! This crate depends only on [`terrabase_causal`]. It has no storage,
//! transport, clock, or asynchronous-runtime dependency. Register candidates
//! carry their own causal evidence and injected HLC metadata, so merging never
//! depends on delivery order.

#![deny(unsafe_code)]
#![deny(missing_docs)]
#![warn(clippy::all, clippy::pedantic)]

mod codec;
mod register;

pub use codec::{CanonicalValue, DecodeError, DecodeLimits, RegisterCodec};
pub use register::{Candidate, CausalIdentity, LwwRegister, MvRegister};
