//! `terrabase-member-transport` — the SE-06 engine-owned member transport.
//!
//! The first real network incarnation of the accepted W4 replication plane
//! on engine-owned transports (charter PD-2026-08-26-13 D2, ADR 0012 D-2):
//! versioned, fail-closed member envelopes crossing in-process lanes where
//! Members share a process boundary, and engine QUIC sockets for networked
//! links — no broker between members, and NATS never carries a member
//! frame.
//!
//! Layout:
//!
//! - [`envelope`]: the message-contract discipline and its wire codec —
//!   schema and version, message/correlation/causation identities,
//!   idempotency keys, ownership epochs, issued/expiry lane ticks, and
//!   payload digests; every violation fails closed.
//! - [`transport`]: the member-transport seam, the W4 fault vocabulary as
//!   per-direction link controls, observable counters, and per-frame-class
//!   bytes-on-wire accounting (PD-13 D1 budgets).
//! - [`in_process`]: the in-process lane over bounded encoded-frame queues.
//! - [`quic`]: the engine-socket lane over real QUIC endpoints with
//!   per-member lane-CA certificates.
//! - [`journal`]: the lane's durable per-Member publication journal — the
//!   lane-scoped stand-in for the Engine publication journal (the
//!   production acquisition seam is the integrator's wiring step).
//! - [`member`]: the W4 member driver semantics (D-1 fencing, D-2
//!   acknowledgement lattice over wire reports, D-3 last-writer-wins) over
//!   the transports.
//! - [`lane`]: the three-Region qualification lane harness and the
//!   composite scenario script.
//! - [`nats_usage`]: the NATS KV/config/discovery usage boundary — the
//!   three allowed roles under their own account and subject namespace,
//!   with the closed record set and the explicit audit that no database
//!   data rides NATS (PD-2026-08-26-13 D2).
//! - [`receipt`]: secret-safe, lane-scoped replication receipts.
//!
//! Non-claims: everything here is lane-scoped. No production topology,
//! timing, bandwidth, or durability capability is implied by anything in
//! this crate; measured values appear only as receipt artifacts.

#![deny(unsafe_code)]
#![deny(missing_docs)]
#![warn(clippy::all, clippy::pedantic)]

pub mod clock;
pub mod envelope;
pub mod identity;
pub mod in_process;
pub mod journal;
pub mod lane;
pub mod member;
pub mod nats_usage;
pub mod production;
pub mod quic;
pub mod receipt;
pub mod transport;

pub use clock::{ClockStamp, LaneInstant};
pub use envelope::{
    BUDGET_BYTES_PER_DELIVERED_CHANGE, BUDGET_ENVELOPE_OVERHEAD_BYTES, ENVELOPE_SCHEMA,
    ENVELOPE_VERSION_MAJOR, ENVELOPE_VERSION_MINOR, EncodedFrame, Envelope, EnvelopeError,
    FrameKind, LaneCommit, LaneDocument, LaneValue, LaneWrite, MAX_FIELD_BYTES, MAX_FRAME_BYTES,
    MAX_PAYLOAD_BYTES, Payload, ReplicatedWrite, decode, encode, peek_kind, peek_payload_len,
};
pub use identity::{
    CollectionName, DocumentId, IdentityError, MemberId, OperationId, RegionId, TenantId,
    TopologyError, TopologyView,
};
pub use in_process::InProcessLane;
pub use journal::{DurableResolution, JournalError, LaneCursor, MemberJournal, RecordOutcome};
pub use lane::{
    LANE_MEMBERS, LANE_RANGE, LANE_REGIONS, LaneHarness, lane_topology, lane_topology_with_owner,
    run_composite_lane,
};
pub use member::{
    AcknowledgementLevel, AcknowledgementReport, ApplyFailure, CatchUpFallback, CommitReceipt,
    ConflictOutcome, ConflictRecord, Convergence, DEFAULT_CATCH_UP_BATCH, DenialSeam, Deposition,
    DispatchReport, EnvelopeRejection, FaultClass, FencedBatch, LaneConfig, LaneMember,
    LaneWriteRequest, MemberError, MemberStatus, RejoinReason, RejoinRejection, ReplicationError,
    ReplicationOutcome, TenantDenial, catch_up_gap, dispatch_report_deliveries, level_satisfied,
    normalize_level, observe_convergence, observe_region_loss, strongest_satisfied_level,
};
pub use quic::{QuicLane, QuicLaneError};
pub use receipt::{
    BYTES_ON_WIRE_DEFINITION, BytesOnWire, CONVERGENCE_DEFINITION, FrameClassBytes, LAG_DEFINITION,
    LaneKind, LaneReceipt, RPO_DEFINITION, RTO_DEFINITION,
};
pub use transport::{
    FrameSelector, LinkControls, LinkStats, MAX_LINK_FRAMES, MemberTransport, TransportError,
};
