//! Sharding as owned ranges over the bucket-range table (ADR 0011).
//!
//! The sharding seam is routing, validation, accounting, and migration
//! execution over the accepted replication plane and `TopologyView` —
//! not a new layer and not an engine-internal orchestrator. The SH wave
//! (SH-02..SH-05) fills this module family packet by packet; until then
//! this root stays declaration-only and proves nothing.

#![allow(dead_code)]
