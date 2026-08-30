//! The TBEP domain registry: the typed frame bodies and closed
//! vocabularies the hosting seam (`crate::host`) serves.
//!
//! Hand-maintained domain source. Under ADR 0025,
//! `proto/terrabase/v1/engine.proto` is the wire contract of record, and the
//! gRPC layer (`crate::grpc`) converts between these domain types and the
//! generated protobuf types. Bounds validation stays fail-closed here so
//! every serving path checks the same limits.

// The registry mirrors one row per vocabulary entry and one struct per
// frame on purpose; the allowances keep the tabular shapes faithful
// instead of fighting the linter over them.
#![allow(clippy::unreadable_literal)]
#![allow(clippy::match_same_arms)]
#![allow(clippy::too_many_lines)]

use crate::value::TbepValue;

/// The protocol identity string.
pub const IDENTITY: &str = "terrabase.engine.protocol";
/// The registry version (additive changes bump the minor).
pub const REGISTRY_VERSION: &str = "1.4";
/// The wire version every frame carries; unknown versions fail closed.
pub const WIRE_VERSION: u8 = 1;
/// The decode bound: larger frames are rejected before any field parses.
pub const MAX_FRAME_BYTES: usize = 16_777_216;
/// The detail bound of `error` frames.
pub const MAX_DETAIL_BYTES: usize = 256;
/// The item bound of every list field.
pub const MAX_LIST_ITEMS: usize = 256;
/// The fixed envelope cost (bytes) over the body, bin8 body header included.
pub const FIXED_ENVELOPE_BYTES: usize = 35;
/// The stream length prefix width (u32 little-endian) of every frame.
pub const STREAM_PREFIX_BYTES: usize = 4;

/// The closed payload-codec vocabulary (named versioned codecs).
pub const PAYLOAD_CODECS: &[&str] = &[
    "terrabase-native/1",
    "terrabase-snapshot/1",
    "terrabase.sync-core/checkpoint@1",
    "terrabase.family-request/1",
    "terrabase.family-response/1",
];

/// The closed family-kind name vocabulary of `family.*` operations.
pub const FAMILY_KINDS: &[&str] = &[
    "attachment",
    "counter",
    "time_series",
    "revisions",
    "compare_exchange",
    "identities",
    "expiration_refresh",
    "archival",
];

/// The written bytes-on-wire budget per frame class (PD-2026-08-26-13 D1).
pub const BUDGETS: &[(&str, usize)] = &[
    ("control", 256),
    ("operation", 1024),
    ("operation_large", 65536),
    ("subscription", 65536),
    ("trigger", 256),
    ("delivery", 65536),
    ("observation", 65536),
    ("schema", 65536),
];

/// The generated `error_code` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ErrorCode {
    /// The `unsupported_version` row (code 1).
    UnsupportedVersion,
    /// The `malformed_frame` row (code 2).
    MalformedFrame,
    /// The `unknown_frame_kind` row (code 3).
    UnknownFrameKind,
    /// The `unknown_operation` row (code 4).
    UnknownOperation,
    /// The `digest_mismatch` row (code 5).
    DigestMismatch,
    /// The `idempotency_pairing` row (code 6).
    IdempotencyPairing,
    /// The `unbound_channel` row (code 7).
    UnboundChannel,
    /// The `already_bound` row (code 8).
    AlreadyBound,
    /// The `invalid_request` row (code 9).
    InvalidRequest,
    /// The `operation_refused` row (code 10).
    OperationRefused,
    /// The `engine_failure` row (code 11).
    EngineFailure,
    /// The `session_refused` row (code 12).
    SessionRefused,
    /// The `subscription_refused` row (code 13).
    SubscriptionRefused,
    /// The `checkpoint_refused` row (code 14).
    CheckpointRefused,
    /// The `shutting_down` row (code 15).
    ShuttingDown,
    /// The `frame_too_large` row (code 16).
    FrameTooLarge,
    /// The `overloaded` row (code 17).
    Overloaded,
    /// The `protocol_version_mismatch` row (code 18).
    ProtocolVersionMismatch,
    /// The `backup_cluster_read_only` row (code 19).
    BackupClusterReadOnly,
    /// The `backup_cluster_immutable` row (code 20).
    BackupClusterImmutable,
    /// The `cold_tier_unavailable` row (code 21).
    ColdTierUnavailable,
    /// The `frontier_unreachable` row (code 22).
    FrontierUnreachable,
    /// The `usage_unavailable` row (code 23).
    UsageUnavailable,
    /// The `usage_corrupt` row (code 24).
    UsageCorrupt,
}

impl ErrorCode {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u16 {
        match self {
            Self::UnsupportedVersion => 1,
            Self::MalformedFrame => 2,
            Self::UnknownFrameKind => 3,
            Self::UnknownOperation => 4,
            Self::DigestMismatch => 5,
            Self::IdempotencyPairing => 6,
            Self::UnboundChannel => 7,
            Self::AlreadyBound => 8,
            Self::InvalidRequest => 9,
            Self::OperationRefused => 10,
            Self::EngineFailure => 11,
            Self::SessionRefused => 12,
            Self::SubscriptionRefused => 13,
            Self::CheckpointRefused => 14,
            Self::ShuttingDown => 15,
            Self::FrameTooLarge => 16,
            Self::Overloaded => 17,
            Self::ProtocolVersionMismatch => 18,
            Self::BackupClusterReadOnly => 19,
            Self::BackupClusterImmutable => 20,
            Self::ColdTierUnavailable => 21,
            Self::FrontierUnreachable => 22,
            Self::UsageUnavailable => 23,
            Self::UsageCorrupt => 24,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u16) -> Option<Self> {
        match code {
            1 => Some(Self::UnsupportedVersion),
            2 => Some(Self::MalformedFrame),
            3 => Some(Self::UnknownFrameKind),
            4 => Some(Self::UnknownOperation),
            5 => Some(Self::DigestMismatch),
            6 => Some(Self::IdempotencyPairing),
            7 => Some(Self::UnboundChannel),
            8 => Some(Self::AlreadyBound),
            9 => Some(Self::InvalidRequest),
            10 => Some(Self::OperationRefused),
            11 => Some(Self::EngineFailure),
            12 => Some(Self::SessionRefused),
            13 => Some(Self::SubscriptionRefused),
            14 => Some(Self::CheckpointRefused),
            15 => Some(Self::ShuttingDown),
            16 => Some(Self::FrameTooLarge),
            17 => Some(Self::Overloaded),
            18 => Some(Self::ProtocolVersionMismatch),
            19 => Some(Self::BackupClusterReadOnly),
            20 => Some(Self::BackupClusterImmutable),
            21 => Some(Self::ColdTierUnavailable),
            22 => Some(Self::FrontierUnreachable),
            23 => Some(Self::UsageUnavailable),
            24 => Some(Self::UsageCorrupt),
            _ => None,
        }
    }
}

/// The generated `acknowledgement` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Acknowledgement {
    /// The `regional_quorum` row (code 1).
    RegionalQuorum,
    /// The `global_quorum` row (code 2).
    GlobalQuorum,
    /// The `backup_ack` row (code 3).
    BackupAck,
}

impl Acknowledgement {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::RegionalQuorum => 1,
            Self::GlobalQuorum => 2,
            Self::BackupAck => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::RegionalQuorum),
            2 => Some(Self::GlobalQuorum),
            3 => Some(Self::BackupAck),
            _ => None,
        }
    }
}

/// The generated `backup_outcome` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum BackupOutcome {
    /// The `none` row (code 1).
    None,
    /// The `full` row (code 2).
    Full,
    /// The `partial` row (code 3).
    Partial,
}

impl BackupOutcome {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::None => 1,
            Self::Full => 2,
            Self::Partial => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::None),
            2 => Some(Self::Full),
            3 => Some(Self::Partial),
            _ => None,
        }
    }
}

/// The generated `read_level` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ReadLevel {
    /// The `local` row (code 1).
    Local,
    /// The `regional_quorum_read` row (code 2).
    RegionalQuorumRead,
    /// The `global_quorum_read` row (code 3).
    GlobalQuorumRead,
}

impl ReadLevel {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Local => 1,
            Self::RegionalQuorumRead => 2,
            Self::GlobalQuorumRead => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Local),
            2 => Some(Self::RegionalQuorumRead),
            3 => Some(Self::GlobalQuorumRead),
            _ => None,
        }
    }
}

/// The generated `cluster_mode` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ClusterMode {
    /// The `standalone` row (code 1).
    Standalone,
    /// The `active` row (code 2).
    Active,
    /// The `backup` row (code 3).
    Backup,
}

impl ClusterMode {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Standalone => 1,
            Self::Active => 2,
            Self::Backup => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Standalone),
            2 => Some(Self::Active),
            3 => Some(Self::Backup),
            _ => None,
        }
    }
}

/// The generated `tier_decision` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum TierDecision {
    /// The `promote` row (code 1).
    Promote,
    /// The `demote` row (code 2).
    Demote,
    /// The `split` row (code 3).
    Split,
    /// The `none` row (code 4).
    None,
    /// The `refused` row (code 5).
    Refused,
}

impl TierDecision {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Promote => 1,
            Self::Demote => 2,
            Self::Split => 3,
            Self::None => 4,
            Self::Refused => 5,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Promote),
            2 => Some(Self::Demote),
            3 => Some(Self::Split),
            4 => Some(Self::None),
            5 => Some(Self::Refused),
            _ => None,
        }
    }
}

/// The generated `expected_version` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ExpectedVersion {
    /// The `absent` row (code 1).
    Absent,
    /// The `any` row (code 2).
    Any,
    /// The `exact` row (code 3).
    Exact(u64),
}

impl ExpectedVersion {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Absent => 1,
            Self::Any => 2,
            Self::Exact(_) => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Absent),
            2 => Some(Self::Any),
            _ => None,
        }
    }
}

/// The generated `write_outcome` closed vocabulary.
#[derive(Clone, Debug, PartialEq)]
pub enum WriteOutcome {
    /// The `committed` row (code 1).
    Committed(CommitReceipt),
    /// The `conflict` row (code 2).
    Conflict,
    /// The `rejected` row (code 3).
    Rejected(WriteReason),
}

impl WriteOutcome {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub fn code(&self) -> u8 {
        match self {
            Self::Committed(_) => 1,
            Self::Conflict => 2,
            Self::Rejected(_) => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            2 => Some(Self::Conflict),
            _ => None,
        }
    }
}

/// The generated `write_reason` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum WriteReason {
    /// The `invalid_document` row (code 1).
    InvalidDocument,
    /// The `schema_refused` row (code 2).
    SchemaRefused,
    /// The `not_found` row (code 3).
    NotFound,
}

impl WriteReason {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::InvalidDocument => 1,
            Self::SchemaRefused => 2,
            Self::NotFound => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::InvalidDocument),
            2 => Some(Self::SchemaRefused),
            3 => Some(Self::NotFound),
            _ => None,
        }
    }
}

/// The generated `query_terminal` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum QueryTerminal {
    /// The `complete` row (code 1).
    Complete,
    /// The `truncated` row (code 2).
    Truncated,
}

impl QueryTerminal {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Complete => 1,
            Self::Truncated => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Complete),
            2 => Some(Self::Truncated),
            _ => None,
        }
    }
}

/// The generated `cancel_outcome` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum CancelOutcome {
    /// The `cancelled` row (code 1).
    Cancelled,
    /// The `already_complete` row (code 2).
    AlreadyComplete,
    /// The `unknown_query` row (code 3).
    UnknownQuery,
}

impl CancelOutcome {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Cancelled => 1,
            Self::AlreadyComplete => 2,
            Self::UnknownQuery => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Cancelled),
            2 => Some(Self::AlreadyComplete),
            3 => Some(Self::UnknownQuery),
            _ => None,
        }
    }
}

/// The generated `change_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ChangeKind {
    /// The `put` row (code 1).
    Put,
    /// The `delete` row (code 2).
    Delete,
}

impl ChangeKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Put => 1,
            Self::Delete => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Put),
            2 => Some(Self::Delete),
            _ => None,
        }
    }
}

/// The generated `subscription_start` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum SubscriptionStart {
    /// The `live` row (code 1).
    Live,
    /// The `sequence` row (code 2).
    Sequence(u64),
}

impl SubscriptionStart {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Live => 1,
            Self::Sequence(_) => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Live),
            _ => None,
        }
    }
}

/// The generated `mutation_outcome` closed vocabulary.
#[derive(Clone, Debug, PartialEq)]
pub enum MutationOutcome {
    /// The `applied` row (code 1).
    Applied(CommitReceipt),
    /// The `rejected` row (code 2).
    Rejected(MutationReason),
}

impl MutationOutcome {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub fn code(&self) -> u8 {
        match self {
            Self::Applied(_) => 1,
            Self::Rejected(_) => 2,
        }
    }
}

/// The generated `mutation_reason` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum MutationReason {
    /// The `scope_refused` row (code 1).
    ScopeRefused,
    /// The `invalid_body` row (code 2).
    InvalidBody,
    /// The `sequence_out_of_order` row (code 3).
    SequenceOutOfOrder,
}

impl MutationReason {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::ScopeRefused => 1,
            Self::InvalidBody => 2,
            Self::SequenceOutOfOrder => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::ScopeRefused),
            2 => Some(Self::InvalidBody),
            3 => Some(Self::SequenceOutOfOrder),
            _ => None,
        }
    }
}

/// The generated `health_state` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum HealthState {
    /// The `starting` row (code 1).
    Starting,
    /// The `serving` row (code 2).
    Serving,
    /// The `draining` row (code 3).
    Draining,
    /// The `stopped` row (code 4).
    Stopped,
}

impl HealthState {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Starting => 1,
            Self::Serving => 2,
            Self::Draining => 3,
            Self::Stopped => 4,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Starting),
            2 => Some(Self::Serving),
            3 => Some(Self::Draining),
            4 => Some(Self::Stopped),
            _ => None,
        }
    }
}

/// The generated `shutdown_reason` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ShutdownReason {
    /// The `operator` row (code 1).
    Operator,
    /// The `supervisor` row (code 2).
    Supervisor,
}

impl ShutdownReason {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Operator => 1,
            Self::Supervisor => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Operator),
            2 => Some(Self::Supervisor),
            _ => None,
        }
    }
}

/// The generated `subscription_close_reason` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum SubscriptionCloseReason {
    /// The `replaced` row (code 1).
    Replaced,
    /// The `shutting_down` row (code 2).
    ShuttingDown,
}

impl SubscriptionCloseReason {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Replaced => 1,
            Self::ShuttingDown => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Replaced),
            2 => Some(Self::ShuttingDown),
            _ => None,
        }
    }
}

/// The generated `binding_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum BindingKind {
    /// The `local_uds` row (code 1).
    LocalUds,
}

impl BindingKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::LocalUds => 1,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::LocalUds),
            _ => None,
        }
    }
}

/// The generated `sync_change` closed vocabulary.
#[derive(Clone, Debug, PartialEq)]
pub enum SyncChange {
    /// The `put` row (code 1).
    Put(Vec<SyncField>),
    /// The `delete` row (code 2).
    Delete,
}

impl SyncChange {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub fn code(&self) -> u8 {
        match self {
            Self::Put(_) => 1,
            Self::Delete => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            2 => Some(Self::Delete),
            _ => None,
        }
    }
}

/// The generated `conflict_outcome_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ConflictOutcomeKind {
    /// The `winner` row (code 1).
    Winner,
    /// The `deposed_owner_retained` row (code 2).
    DeposedOwnerRetained,
    /// The `untrusted_clock` row (code 3).
    UntrustedClock,
}

impl ConflictOutcomeKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Winner => 1,
            Self::DeposedOwnerRetained => 2,
            Self::UntrustedClock => 3,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Winner),
            2 => Some(Self::DeposedOwnerRetained),
            3 => Some(Self::UntrustedClock),
            _ => None,
        }
    }
}

/// The generated `denial_seam` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum DenialSeam {
    /// The `serve_request` row (code 1).
    ServeRequest,
    /// The `apply_delivery` row (code 2).
    ApplyDelivery,
}

impl DenialSeam {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::ServeRequest => 1,
            Self::ApplyDelivery => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::ServeRequest),
            2 => Some(Self::ApplyDelivery),
            _ => None,
        }
    }
}

/// The generated `replication_status` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ReplicationStatus {
    /// The `assigned` row (code 1).
    Assigned,
    /// The `active` row (code 2).
    Active,
    /// The `responsive` row (code 3).
    Responsive,
    /// The `stale` row (code 4).
    Stale,
    /// The `unavailable` row (code 5).
    Unavailable,
    /// The `degraded` row (code 6).
    Degraded,
    /// The `unknown` row (code 7).
    Unknown,
}

impl ReplicationStatus {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Assigned => 1,
            Self::Active => 2,
            Self::Responsive => 3,
            Self::Stale => 4,
            Self::Unavailable => 5,
            Self::Degraded => 6,
            Self::Unknown => 7,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Assigned),
            2 => Some(Self::Active),
            3 => Some(Self::Responsive),
            4 => Some(Self::Stale),
            5 => Some(Self::Unavailable),
            6 => Some(Self::Degraded),
            7 => Some(Self::Unknown),
            _ => None,
        }
    }
}

/// The generated `violation_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ViolationKind {
    /// The `document_not_object` row (code 1).
    DocumentNotObject,
    /// The `missing_required_field` row (code 2).
    MissingRequiredField,
    /// The `type_mismatch` row (code 3).
    TypeMismatch,
    /// The `value_not_allowed` row (code 4).
    ValueNotAllowed,
    /// The `number_out_of_range` row (code 5).
    NumberOutOfRange,
    /// The `length_out_of_range` row (code 6).
    LengthOutOfRange,
}

impl ViolationKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::DocumentNotObject => 1,
            Self::MissingRequiredField => 2,
            Self::TypeMismatch => 3,
            Self::ValueNotAllowed => 4,
            Self::NumberOutOfRange => 5,
            Self::LengthOutOfRange => 6,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::DocumentNotObject),
            2 => Some(Self::MissingRequiredField),
            3 => Some(Self::TypeMismatch),
            4 => Some(Self::ValueNotAllowed),
            5 => Some(Self::NumberOutOfRange),
            6 => Some(Self::LengthOutOfRange),
            _ => None,
        }
    }
}

/// The generated `coercion` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Coercion {
    /// The `integer_to_text` row (code 1).
    IntegerToText,
    /// The `text_to_integer` row (code 2).
    TextToInteger,
    /// The `integer_to_number` row (code 3).
    IntegerToNumber,
    /// The `number_to_integer` row (code 4).
    NumberToInteger,
}

impl Coercion {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::IntegerToText => 1,
            Self::TextToInteger => 2,
            Self::IntegerToNumber => 3,
            Self::NumberToInteger => 4,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::IntegerToText),
            2 => Some(Self::TextToInteger),
            3 => Some(Self::IntegerToNumber),
            4 => Some(Self::NumberToInteger),
            _ => None,
        }
    }
}

/// The generated `transform_rejection` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum TransformRejection {
    /// The `value_not_coercible` row (code 1).
    ValueNotCoercible,
    /// The `target_field_occupied` row (code 2).
    TargetFieldOccupied,
}

impl TransformRejection {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::ValueNotCoercible => 1,
            Self::TargetFieldOccupied => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::ValueNotCoercible),
            2 => Some(Self::TargetFieldOccupied),
            _ => None,
        }
    }
}

/// The generated `rollback_limit_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum RollbackLimitKind {
    /// The `reversible` row (code 1).
    Reversible,
    /// The `not_reversible` row (code 2).
    NotReversible,
}

impl RollbackLimitKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::Reversible => 1,
            Self::NotReversible => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::Reversible),
            2 => Some(Self::NotReversible),
            _ => None,
        }
    }
}

/// The generated `migration_state_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum MigrationStateKind {
    /// The `none` row (code 1).
    None,
    /// The `in_progress` row (code 2).
    InProgress,
    /// The `rolling_back` row (code 3).
    RollingBack,
    /// The `complete` row (code 4).
    Complete,
}

impl MigrationStateKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::None => 1,
            Self::InProgress => 2,
            Self::RollingBack => 3,
            Self::Complete => 4,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::None),
            2 => Some(Self::InProgress),
            3 => Some(Self::RollingBack),
            4 => Some(Self::Complete),
            _ => None,
        }
    }
}

/// The generated `plan_problem_kind` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum PlanProblemKind {
    /// The `transform_rejected` row (code 1).
    TransformRejected,
    /// The `target_violations` row (code 2).
    TargetViolations,
}

impl PlanProblemKind {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::TransformRejected => 1,
            Self::TargetViolations => 2,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::TransformRejected),
            2 => Some(Self::TargetViolations),
            _ => None,
        }
    }
}

/// The generated `transform_literal` closed vocabulary.
#[derive(Clone, Debug, PartialEq)]
pub enum TransformLiteral {
    /// The `boolean` row (code 1).
    Boolean(bool),
    /// The `integer` row (code 2).
    Integer(i64),
    /// The `text` row (code 3).
    Text(String),
}

impl TransformLiteral {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub fn code(&self) -> u8 {
        match self {
            Self::Boolean(_) => 1,
            Self::Integer(_) => 2,
            Self::Text(_) => 3,
        }
    }
}

/// The generated `field_transform` closed vocabulary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum FieldTransform {
    /// The `add_field` row (code 1).
    AddField,
    /// The `remove_field` row (code 2).
    RemoveField,
    /// The `rename_field` row (code 3).
    RenameField,
    /// The `retype_field` row (code 4).
    RetypeField,
}

impl FieldTransform {
    /// The variant's wire code; unknown codes fail closed on decode.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::AddField => 1,
            Self::RemoveField => 2,
            Self::RenameField => 3,
            Self::RetypeField => 4,
        }
    }

    /// Decodes a payload-less variant from its wire code.
    ///
    /// Payload-carrying variants decode through the serde codec
    /// (`from_wire`); they answer `None` here by construction.
    #[must_use]
    pub const fn from_code(code: u8) -> Option<Self> {
        match code {
            1 => Some(Self::AddField),
            2 => Some(Self::RemoveField),
            3 => Some(Self::RenameField),
            4 => Some(Self::RetypeField),
            _ => None,
        }
    }
}

/// The `document_body` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentBody {
    /// The `id` field.
    pub id: String,
    /// The `collection` field.
    pub collection: String,
    /// The `version` field.
    pub version: u64,
    /// The `body` field.
    pub body: TbepValue,
}

/// The `document_put` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentPut {
    /// The `id` field.
    pub id: String,
    /// The `collection` field.
    pub collection: String,
    /// The `body` field.
    pub body: TbepValue,
}

/// The `batch_write` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BatchWrite {
    /// The `document` field.
    pub document: DocumentPut,
    /// The `expected` field.
    pub expected: ExpectedVersion,
    /// The `operation_id` field.
    pub operation_id: String,
}

/// The `commit_receipt` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct CommitReceipt {
    /// The `sequence` field.
    pub sequence: u64,
    /// The `version` field.
    pub version: u64,
    /// The `operation_id` field.
    pub operation_id: String,
    /// The `acknowledgement` field.
    pub acknowledgement: Option<Acknowledgement>,
    /// The `backup` field.
    pub backup: Option<BackupOutcome>,
}

/// The `tier_move_reason` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierMoveReason {
    /// The `fact` field.
    pub fact: String,
    /// The `observed` field.
    pub observed: String,
}

/// The `tier_move_receipt` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierMoveReceipt {
    /// The `policy_id` field.
    pub policy_id: String,
    /// The `policy_version` field.
    pub policy_version: u32,
    /// The `decision` field.
    pub decision: TierDecision,
    /// The `reasons` field.
    pub reasons: Vec<TierMoveReason>,
    /// The `segment_id` field.
    pub segment_id: u64,
    /// The `bytes` field.
    pub bytes: u64,
    /// The `manifest_before` field.
    pub manifest_before: u64,
    /// The `manifest_after` field.
    pub manifest_after: u64,
    /// The `dry_run` field.
    pub dry_run: bool,
    /// The `manifest_accepted` field.
    pub manifest_accepted: bool,
}

/// The `change_event` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ChangeEvent {
    /// The `sequence` field.
    pub sequence: u64,
    /// The `operation_id` field.
    pub operation_id: String,
    /// The `kind` field.
    pub kind: ChangeKind,
    /// The `collection` field.
    pub collection: String,
    /// The `document` field.
    pub document: String,
}

/// The `sync_field` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncField {
    /// The `name` field.
    pub name: String,
    /// The `value` field.
    pub value: TbepValue,
}

/// The `clock_stamp` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ClockStamp {
    /// The `ticks` field.
    pub ticks: u64,
    /// The `counter` field.
    pub counter: u64,
}

/// The `conflict_outcome` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ConflictOutcome {
    /// The `kind` field.
    pub kind: ConflictOutcomeKind,
    /// The `member` field.
    pub member: String,
}

/// The `conflict_record` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ConflictRecord {
    /// The `tenant` field.
    pub tenant: String,
    /// The `operations` field.
    pub operations: Vec<String>,
    /// The `members` field.
    pub members: Vec<String>,
    /// The `clocks` field.
    pub clocks: Vec<ClockStamp>,
    /// The `outcome` field.
    pub outcome: ConflictOutcome,
}

/// The `fenced_record` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct FencedRecord {
    /// The `range` field.
    pub range: String,
    /// The `batch_epoch` field.
    pub batch_epoch: u64,
    /// The `highest_epoch` field.
    pub highest_epoch: u64,
    /// The `source` field.
    pub source: String,
}

/// The `deposition_record` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DepositionRecord {
    /// The `range` field.
    pub range: String,
    /// The `held_epoch` field.
    pub held_epoch: u64,
    /// The `owner_epoch` field.
    pub owner_epoch: u64,
    /// The `new_owner` field.
    pub new_owner: String,
}

/// The `denial_record` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DenialRecord {
    /// The `seam` field.
    pub seam: DenialSeam,
    /// The `scope_tenant` field.
    pub scope_tenant: String,
    /// The `attempted_tenant` field.
    pub attempted_tenant: String,
    /// The `counterparty` field.
    pub counterparty: String,
}

/// The `schema_violation` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaViolation {
    /// The `field` field.
    pub field: String,
    /// The `kind` field.
    pub kind: ViolationKind,
}

/// The `plan_problem` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct PlanProblem {
    /// The `document` field.
    pub document: String,
    /// The `field` field.
    pub field: String,
    /// The `kind` field.
    pub kind: PlanProblemKind,
    /// The `rejection` field.
    pub rejection: Option<TransformRejection>,
    /// The `violations` field.
    pub violations: Vec<SchemaViolation>,
}

/// The `transform_counts` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TransformCounts {
    /// The `added` field.
    pub added: u64,
    /// The `removed` field.
    pub removed: u64,
    /// The `renamed` field.
    pub renamed: u64,
    /// The `retyped` field.
    pub retyped: u64,
}

/// The `migration_state` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationState {
    /// The `kind` field.
    pub kind: MigrationStateKind,
    /// The `manifest` field.
    pub manifest: String,
    /// The `from_version` field.
    pub from_version: u64,
    /// The `to_version` field.
    pub to_version: u64,
    /// The `documents` field.
    pub documents: u64,
    /// The `transformed` field.
    pub transformed: u64,
    /// The `pages` field.
    pub pages: u64,
}

/// The `migration_plan` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationPlan {
    /// The `manifest` field.
    pub manifest: String,
    /// The `collection` field.
    pub collection: String,
    /// The `from_version` field.
    pub from_version: u64,
    /// The `to_version` field.
    pub to_version: u64,
    /// The `ready` field.
    pub ready: bool,
    /// The `documents` field.
    pub documents: u64,
    /// The `source_digest` field.
    pub source_digest: String,
    /// The `result_digest` field.
    pub result_digest: String,
    /// The `transform_counts` field.
    pub transform_counts: TransformCounts,
    /// The `problems` field.
    pub problems: Vec<PlanProblem>,
    /// The `problems_truncated` field.
    pub problems_truncated: bool,
}

/// The `migration_receipt` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationReceipt {
    /// The `collection` field.
    pub collection: String,
    /// The `manifest` field.
    pub manifest: String,
    /// The `from_version` field.
    pub from_version: u64,
    /// The `to_version` field.
    pub to_version: u64,
    /// The `source_digest` field.
    pub source_digest: String,
    /// The `result_digest` field.
    pub result_digest: String,
    /// The `documents` field.
    pub documents: u64,
    /// The `transformed` field.
    pub transformed: u64,
    /// The `transform_counts` field.
    pub transform_counts: TransformCounts,
    /// The `pages` field.
    pub pages: u64,
    /// The `rollback_limit` field.
    pub rollback_limit: RollbackLimitKind,
    /// The `acknowledged_not_rollbackable` field.
    pub acknowledged_not_rollbackable: bool,
    /// The `already_applied` field.
    pub already_applied: bool,
}

/// The `schema_migration_contract` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaMigrationContract {
    /// The `collection` field.
    pub collection: String,
    /// The `from_version` field.
    pub from_version: u64,
    /// The `to_version` field.
    pub to_version: u64,
    /// The `target` field.
    pub target: TbepValue,
    /// The `transforms` field.
    pub transforms: Vec<FieldTransformStep>,
}

/// The `field_transform_step` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct FieldTransformStep {
    /// The `kind` field.
    pub kind: FieldTransform,
    /// The `field` field.
    pub field: String,
    /// The `to` field.
    pub to: Option<String>,
    /// The `default` field.
    pub default: Option<TransformLiteral>,
    /// The `coercion` field.
    pub coercion: Option<Coercion>,
}

/// The `channel_bind_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ChannelBindRequest {
    /// The `principal` field.
    pub principal: String,
    /// The `tenant` field.
    pub tenant: String,
}

impl ChannelBindRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.principal, 128, "principal")?;
        crate::value::check_str_bound(&self.tenant, 64, "tenant")?;
        Ok(())
    }
}

/// The `channel_bind_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ChannelBindResponse {
    /// The `tenant` field.
    pub tenant: String,
    /// The `serving_instance` field.
    pub serving_instance: u64,
}

impl ChannelBindResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.tenant, 64, "tenant")?;
        Ok(())
    }
}

/// The `document_read_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentReadRequest {
    /// The `collection` field.
    pub collection: String,
    /// The `document` field.
    pub document: String,
    /// The `level` field.
    pub level: Option<ReadLevel>,
}

impl DocumentReadRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        crate::value::check_str_bound(&self.document, 64, "document")?;
        Ok(())
    }
}

/// The `document_read_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentReadResponse {
    /// The `found` field.
    pub found: bool,
    /// The `document` field.
    pub document: Option<DocumentBody>,
    /// The `position` field.
    pub position: Option<u64>,
}

impl DocumentReadResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `document_write_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentWriteRequest {
    /// The `document` field.
    pub document: DocumentPut,
    /// The `expected` field.
    pub expected: ExpectedVersion,
    /// The `operation_id` field.
    pub operation_id: String,
    /// The `ack` field.
    pub ack: Option<Acknowledgement>,
    /// Trusted usage admission attached to the same transaction.
    pub usage: Option<UsageAdmissionContext>,
}

impl DocumentWriteRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.operation_id, 128, "operation_id")?;
        Ok(())
    }
}

/// The `document_write_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentWriteResponse {
    /// The `outcome` field.
    pub outcome: WriteOutcome,
}

impl DocumentWriteResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `document_delete_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentDeleteRequest {
    /// The `collection` field.
    pub collection: String,
    /// The `document` field.
    pub document: String,
    /// The `expected` field.
    pub expected: ExpectedVersion,
    /// The `operation_id` field.
    pub operation_id: String,
    /// The `ack` field.
    pub ack: Option<Acknowledgement>,
    /// Trusted usage admission attached to the same transaction.
    pub usage: Option<UsageAdmissionContext>,
}

impl DocumentDeleteRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        crate::value::check_str_bound(&self.document, 64, "document")?;
        crate::value::check_str_bound(&self.operation_id, 128, "operation_id")?;
        Ok(())
    }
}

/// The `document_delete_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentDeleteResponse {
    /// The `outcome` field.
    pub outcome: WriteOutcome,
}

impl DocumentDeleteResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `document_batch_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentBatchRequest {
    /// The `items` field.
    pub items: Vec<BatchWrite>,
    /// The `ack` field.
    pub ack: Option<Acknowledgement>,
    /// Trusted usage admission attached to the same transaction.
    pub usage: Option<UsageAdmissionContext>,
}

impl DocumentBatchRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(&self.items, MAX_LIST_ITEMS, "items")?;
        Ok(())
    }
}

/// The `document_batch_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentBatchResponse {
    /// The `outcome` field.
    pub outcome: WriteOutcome,
}

impl DocumentBatchResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `query_execute_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryExecuteRequest {
    /// The `query_id` field.
    pub query_id: u64,
    /// The `text` field.
    pub text: String,
    /// The `max_items` field.
    pub max_items: u32,
    /// The `level` field.
    pub level: Option<ReadLevel>,
}

impl QueryExecuteRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.text, 16384, "text")?;
        Ok(())
    }
}

/// The `query_execute_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryExecuteResponse {
    /// The `documents` field.
    pub documents: Vec<DocumentBody>,
    /// The `count` field.
    pub count: u32,
    /// The `terminal` field.
    pub terminal: QueryTerminal,
    /// The `position` field.
    pub position: Option<u64>,
}

impl QueryExecuteResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(&self.documents, MAX_LIST_ITEMS, "documents")?;
        Ok(())
    }
}

/// The `query_cancel_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryCancelRequest {
    /// The `query_id` field.
    pub query_id: u64,
}

impl QueryCancelRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `query_cancel_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryCancelResponse {
    /// The `outcome` field.
    pub outcome: CancelOutcome,
}

impl QueryCancelResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `family_read_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct FamilyReadRequest {
    /// The `family` field.
    pub family: String,
    /// The `name` field.
    pub name: String,
    /// The `request_codec` field.
    pub request_codec: String,
    /// The `request` field.
    pub request: Vec<u8>,
}

impl FamilyReadRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.family, 64, "family")?;
        crate::value::check_one_of(&self.family, FAMILY_KINDS, "family")?;
        crate::value::check_str_bound(&self.name, 128, "name")?;
        crate::value::check_str_bound(&self.request_codec, 64, "request_codec")?;
        Ok(())
    }
}

/// The `family_read_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct FamilyReadResponse {
    /// The `response_codec` field.
    pub response_codec: String,
    /// The `response` field.
    pub response: Vec<u8>,
}

impl FamilyReadResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.response_codec, 64, "response_codec")?;
        Ok(())
    }
}

/// The `family_write_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct FamilyWriteRequest {
    /// The `family` field.
    pub family: String,
    /// The `name` field.
    pub name: String,
    /// The `request_codec` field.
    pub request_codec: String,
    /// The `request` field.
    pub request: Vec<u8>,
    /// The `operation_id` field.
    pub operation_id: String,
    /// Trusted usage admission attached to the same transaction.
    pub usage: Option<UsageAdmissionContext>,
}

impl FamilyWriteRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.family, 64, "family")?;
        crate::value::check_one_of(&self.family, FAMILY_KINDS, "family")?;
        crate::value::check_str_bound(&self.name, 128, "name")?;
        crate::value::check_str_bound(&self.request_codec, 64, "request_codec")?;
        crate::value::check_str_bound(&self.operation_id, 128, "operation_id")?;
        Ok(())
    }
}

/// The `family_write_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct FamilyWriteResponse {
    /// The `response_codec` field.
    pub response_codec: String,
    /// The `response` field.
    pub response: Vec<u8>,
}

impl FamilyWriteResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.response_codec, 64, "response_codec")?;
        Ok(())
    }
}

/// The `backbone_subscribe_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BackboneSubscribeRequest {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `collections` field.
    pub collections: Option<Vec<String>>,
    /// The `from` field.
    pub from: SubscriptionStart,
}

impl BackboneSubscribeRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        if let Some(value) = &self.collections {
            crate::value::check_list_bound(value, MAX_LIST_ITEMS, "collections")?;
        }
        Ok(())
    }
}

/// The `backbone_subscribe_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BackboneSubscribeResponse {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `start_sequence` field.
    pub start_sequence: u64,
}

impl BackboneSubscribeResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `backbone_acknowledge_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BackboneAcknowledgeRequest {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `through_sequence` field.
    pub through_sequence: u64,
}

impl BackboneAcknowledgeRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `backbone_acknowledge_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BackboneAcknowledgeResponse {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `acknowledged_through` field.
    pub acknowledged_through: u64,
}

impl BackboneAcknowledgeResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `backbone_unsubscribe_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BackboneUnsubscribeRequest {
    /// The `subscription_id` field.
    pub subscription_id: u64,
}

impl BackboneUnsubscribeRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `backbone_unsubscribe_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct BackboneUnsubscribeResponse {
    /// The `subscription_id` field.
    pub subscription_id: u64,
}

impl BackboneUnsubscribeResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_session_setup_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncSessionSetupRequest {
    /// The `collection` field.
    pub collection: String,
    /// The `resume` field.
    pub resume: Option<Vec<u8>>,
}

impl SyncSessionSetupRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        Ok(())
    }
}

/// The `sync_session_setup_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncSessionSetupResponse {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `checkpoint` field.
    pub checkpoint: Vec<u8>,
    /// The `retained_floor` field.
    pub retained_floor: Vec<u8>,
}

impl SyncSessionSetupResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_delta_pull_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncDeltaPullRequest {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `from` field.
    pub from: Vec<u8>,
}

impl SyncDeltaPullRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_delta_pull_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncDeltaPullResponse {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `payload_codec` field.
    pub payload_codec: String,
    /// The `payload` field.
    pub payload: Vec<u8>,
    /// The `next` field.
    pub next: Vec<u8>,
    /// The `commits` field.
    pub commits: u32,
    /// The `single_commit_oversize` field.
    pub single_commit_oversize: bool,
}

impl SyncDeltaPullResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.payload_codec, 64, "payload_codec")?;
        Ok(())
    }
}

/// The `sync_snapshot_pull_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncSnapshotPullRequest {
    /// The `session_id` field.
    pub session_id: u64,
}

impl SyncSnapshotPullRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_snapshot_pull_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncSnapshotPullResponse {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `payload_codec` field.
    pub payload_codec: String,
    /// The `payload` field.
    pub payload: Vec<u8>,
    /// The `checkpoint` field.
    pub checkpoint: Vec<u8>,
}

impl SyncSnapshotPullResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.payload_codec, 64, "payload_codec")?;
        Ok(())
    }
}

/// The `sync_checkpoint_ack_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncCheckpointAckRequest {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `checkpoint` field.
    pub checkpoint: Vec<u8>,
}

impl SyncCheckpointAckRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_checkpoint_ack_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncCheckpointAckResponse {
    /// The `session_id` field.
    pub session_id: u64,
}

impl SyncCheckpointAckResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_mutation_submit_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncMutationSubmitRequest {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `document` field.
    pub document: String,
    /// The `change` field.
    pub change: SyncChange,
    /// Trusted usage admission attached to the same transaction.
    pub usage: Option<UsageAdmissionContext>,
}

impl SyncMutationSubmitRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.document, 64, "document")?;
        Ok(())
    }
}

/// The `sync_mutation_submit_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncMutationSubmitResponse {
    /// The `session_id` field.
    pub session_id: u64,
    /// The `outcome` field.
    pub outcome: MutationOutcome,
}

impl SyncMutationSubmitResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_session_close_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncSessionCloseRequest {
    /// The `session_id` field.
    pub session_id: u64,
}

impl SyncSessionCloseRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `sync_session_close_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SyncSessionCloseResponse {
    /// The `session_id` field.
    pub session_id: u64,
}

impl SyncSessionCloseResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `topology_read_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TopologyReadRequest {}

impl TopologyReadRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `topology_read_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TopologyReadResponse {
    /// The `binding` field.
    pub binding: BindingKind,
    /// The `subsystems` field.
    pub subsystems: Vec<String>,
    /// The `engine_topology_available` field.
    pub engine_topology_available: bool,
    /// The `cluster_mode` field.
    pub cluster_mode: Option<ClusterMode>,
}

impl TopologyReadResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(&self.subsystems, MAX_LIST_ITEMS, "subsystems")?;
        Ok(())
    }
}

/// The `admin_status_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct AdminStatusRequest {}

impl AdminStatusRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `admin_status_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct AdminStatusResponse {
    /// The `registry_version` field.
    pub registry_version: String,
    /// The `frames_served` field.
    pub frames_served: u64,
    /// The `sessions` field.
    pub sessions: u32,
    /// The `subscriptions` field.
    pub subscriptions: u32,
    /// The `scopes` field.
    pub scopes: u32,
}

impl AdminStatusResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.registry_version, 32, "registry_version")?;
        Ok(())
    }
}

/// The closed usage meter vocabulary.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum UsageMeter {
    /// Units assigned by the admitted-request cost table.
    AdmittedRequestUnits,
    /// Public response and change-stream bytes.
    PublicEgressBytes,
}

/// The integer unit of one usage fact.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UsageUnit {
    /// One admitted request cost unit.
    RequestUnit,
    /// One public byte.
    Byte,
}

/// The scope at which one usage meter charges.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UsageChargeScope {
    /// One admitted request.
    AdmittedRequest,
    /// One public response or change-stream delivery.
    PublicEgress,
}

/// The stable event identity that controls usage minting.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum UsageMintingRule {
    /// Mint once for a stable admitted request identity.
    StableAdmittedRequest,
    /// Mint once for one public response or change-stream delivery.
    PublicResponse,
}

/// The closed airport-code Region vocabulary accepted by usage accounting.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum UsageRegion {
    /// Sao Paulo, Brazil.
    Gru,
    /// San Francisco, United States.
    Sfo,
    /// Frankfurt, Germany.
    Fra,
}

/// Maximum admitted-request units carried by one fact.
pub const MAX_ADMITTED_REQUEST_UNITS_PER_FACT: i128 = 1_000_000;

/// Maximum public-egress bytes carried by one fact.
pub const MAX_PUBLIC_EGRESS_BYTES_PER_FACT: i128 = 1_099_511_627_776;

impl UsageMintingRule {
    /// Returns the protocol discriminant used in stable fact ordering.
    #[must_use]
    pub const fn code(self) -> u8 {
        match self {
            Self::StableAdmittedRequest => 2,
            Self::PublicResponse => 3,
        }
    }
}

impl UsageMeter {
    /// Returns the fixed unit, charge scope, and minting rule of this meter.
    #[must_use]
    pub const fn contract(self) -> (UsageUnit, UsageChargeScope, UsageMintingRule) {
        match self {
            Self::AdmittedRequestUnits => (
                UsageUnit::RequestUnit,
                UsageChargeScope::AdmittedRequest,
                UsageMintingRule::StableAdmittedRequest,
            ),
            Self::PublicEgressBytes => {
                (UsageUnit::Byte, UsageChargeScope::PublicEgress, UsageMintingRule::PublicResponse)
            }
        }
    }
}

/// Trusted usage admission attached atomically to one origin mutation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageAdmissionContext {
    /// The Project identity.
    pub project: String,
    /// The Database identity.
    pub database: String,
    /// The Partition identity.
    pub partition: String,
    /// The Region that admitted the request.
    pub origin_region: UsageRegion,
    /// The stable admitted request identity.
    pub request_id: String,
    /// The trusted admission time in Unix milliseconds.
    pub accepted_at_millis: u64,
    /// The immutable admitted-request meter version.
    pub meter_version: u32,
    /// The canonical positive i128 request units.
    pub integer_units: String,
}

impl UsageAdmissionContext {
    /// Validates the trusted scope, time, meter, and retry-identity pairing.
    ///
    /// # Errors
    ///
    /// Returns the first invalid or out-of-bounds field.
    pub fn validate_for_key(&self, idempotency_key: &str) -> Result<(), crate::value::BodyError> {
        usage_identifier(&self.project, 255, "project")?;
        usage_identifier(&self.database, 255, "database")?;
        usage_identifier(&self.partition, 255, "partition")?;
        usage_identifier(&self.request_id, 128, "request_id")?;
        if self.request_id != idempotency_key
            || self.accepted_at_millis == 0
            || self.meter_version == 0
        {
            return Err(invalid_usage("usage_admission"));
        }
        let units = positive_i128(&self.integer_units, "integer_units")?;
        if units > MAX_ADMITTED_REQUEST_UNITS_PER_FACT {
            return Err(invalid_usage("integer_units"));
        }
        Ok(())
    }
}

/// The closed meter set accepted by standalone usage recording.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UsageRecordMeter {
    /// Admitted request units for a read or query.
    AdmittedRequestUnits,
    /// Public response or change-stream bytes.
    PublicEgressBytes,
}

impl UsageRecordMeter {
    const fn unit_limit(self) -> i128 {
        match self {
            Self::AdmittedRequestUnits => MAX_ADMITTED_REQUEST_UNITS_PER_FACT,
            Self::PublicEgressBytes => MAX_PUBLIC_EGRESS_BYTES_PER_FACT,
        }
    }
}

/// One bounded, idempotent standalone usage record.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageRecordRequest {
    /// The admitted standalone meter.
    pub meter: UsageRecordMeter,
    /// The Project identity.
    pub project: String,
    /// The Database identity.
    pub database: String,
    /// The Partition identity.
    pub partition: String,
    /// The Region that observed the work.
    pub origin_region: UsageRegion,
    /// The stable fact identity.
    pub fact_id: String,
    /// The trusted recording time in Unix milliseconds.
    pub recorded_at_millis: u64,
    /// The immutable meter version.
    pub meter_version: u32,
    /// The canonical positive i128 units.
    pub integer_units: String,
}

impl UsageRecordRequest {
    /// Validates the record and its idempotency-key pairing.
    ///
    /// # Errors
    ///
    /// Returns the first invalid or out-of-bounds field.
    pub fn validate_for_key(&self, idempotency_key: &str) -> Result<(), crate::value::BodyError> {
        usage_identifier(&self.project, 255, "project")?;
        usage_identifier(&self.database, 255, "database")?;
        usage_identifier(&self.partition, 255, "partition")?;
        usage_identifier(&self.fact_id, 128, "fact_id")?;
        if self.fact_id != idempotency_key
            || self.recorded_at_millis == 0
            || self.meter_version == 0
        {
            return Err(invalid_usage("usage_record"));
        }
        let units = positive_i128(&self.integer_units, "integer_units")?;
        if units > self.meter.unit_limit() {
            return Err(invalid_usage("integer_units"));
        }
        Ok(())
    }
}

/// The durable receipt of one standalone usage record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct UsageRecordResponse {
    /// The durable usage sequence.
    pub sequence: u64,
    /// The replica-set epoch.
    pub replica_epoch: u64,
    /// Whether this request replayed the already durable fact.
    pub already_recorded: bool,
}

impl UsageRecordResponse {
    /// Validates the durable receipt.
    ///
    /// # Errors
    ///
    /// Returns an error when a durable identity is zero.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        if self.sequence == 0 || self.replica_epoch == 0 {
            return Err(invalid_usage("usage_record_receipt"));
        }
        Ok(())
    }
}

/// One WAL-durable, canonically encoded usage fact.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageFact {
    /// The stable minting identity.
    pub fact_id: String,
    /// The closed meter kind.
    pub meter: UsageMeter,
    /// The immutable meter version.
    pub meter_version: u32,
    /// The meter's integer unit.
    pub unit: UsageUnit,
    /// The meter's charge scope.
    pub charge_scope: UsageChargeScope,
    /// The minting rule represented by the stable identity.
    pub minting_rule: UsageMintingRule,
    /// The tenant identity, which must match the RPC metadata binding.
    pub tenant: String,
    /// The Project identity.
    pub project: String,
    /// The Database identity.
    pub database: String,
    /// The Partition identity.
    pub partition: String,
    /// The origin Region identity.
    pub region: UsageRegion,
    /// The inclusive measurement-window start in Unix milliseconds.
    pub measurement_start_millis: u64,
    /// The exclusive measurement-window end in Unix milliseconds.
    pub measurement_end_millis: u64,
    /// The canonical signed i128 delta.
    pub integer_delta: String,
    /// The WAL commit sequence.
    pub sequence: u64,
    /// The replica-set epoch.
    pub replica_epoch: u64,
    /// The canonical encoding version.
    pub format_version: u32,
    /// The canonical durable fact envelope.
    pub canonical: Vec<u8>,
    /// The canonical envelope checksum.
    pub checksum: Vec<u8>,
}

impl UsageFact {
    /// Validates the usage vocabulary, bounds, and canonical envelope shape.
    ///
    /// # Errors
    ///
    /// Returns the first invalid or out-of-bounds field.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        usage_identifier(&self.fact_id, 255, "fact_id")?;
        usage_identifier(&self.tenant, 64, "tenant")?;
        usage_identifier(&self.project, 255, "project")?;
        usage_identifier(&self.database, 255, "database")?;
        usage_identifier(&self.partition, 255, "partition")?;
        if self.meter_version == 0
            || self.sequence == 0
            || self.replica_epoch == 0
            || self.format_version == 0
            || (self.unit, self.charge_scope, self.minting_rule) != self.meter.contract()
            || self.measurement_end_millis < self.measurement_start_millis
        {
            return Err(invalid_usage("usage_fact"));
        }
        crate::value::check_str_bound(&self.integer_delta, 40, "integer_delta")?;
        let delta =
            self.integer_delta.parse::<i128>().map_err(|_| invalid_usage("integer_delta"))?;
        if delta == 0 || delta.to_string() != self.integer_delta {
            return Err(invalid_usage("integer_delta"));
        }
        usage_bytes(&self.canonical, 65_536, "canonical")?;
        if self.checksum.len() != 32 || !self.canonical.ends_with(&self.checksum) {
            return Err(invalid_usage("checksum"));
        }
        Ok(())
    }
}

/// A tenant-bound stable-snapshot usage export request.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageExportRequest {
    /// Optional inclusive half-open window start.
    pub window_start_millis: Option<u64>,
    /// Optional exclusive half-open window end.
    pub window_end_millis: Option<u64>,
    /// Requested page size.
    pub page_size: u32,
    /// Opaque continuation from the preceding page.
    pub continuation: Vec<u8>,
}

impl UsageExportRequest {
    /// Validates the all-or-none window, page, and continuation bounds.
    ///
    /// # Errors
    ///
    /// Returns the first invalid or out-of-bounds field.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        match (self.window_start_millis, self.window_end_millis) {
            (None, None) => {}
            (Some(start), Some(end)) if start < end => {}
            _ => return Err(invalid_usage("window")),
        }
        if self.page_size == 0
            || usize::try_from(self.page_size).unwrap_or(usize::MAX) > MAX_LIST_ITEMS
        {
            return Err(invalid_usage("page_size"));
        }
        usage_bytes(&self.continuation, 1024, "continuation")
    }
}

/// One stable-snapshot page of authoritative usage facts.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageExportResponse {
    /// Facts ordered by sequence, minting rule, and stable identity.
    pub facts: Vec<UsageFact>,
    /// Opaque continuation; empty when the snapshot is complete.
    pub continuation: Vec<u8>,
    /// The durable high-water fixed by the first page.
    pub snapshot_sequence: u64,
}

impl UsageExportResponse {
    /// Validates response bounds, fact shapes, and strict order.
    ///
    /// # Errors
    ///
    /// Returns the first invalid or out-of-bounds field.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(&self.facts, MAX_LIST_ITEMS, "facts")?;
        usage_bytes(&self.continuation, 1024, "continuation")?;
        let mut previous: Option<(u64, u8, &str)> = None;
        for fact in &self.facts {
            fact.validate()?;
            if fact.sequence > self.snapshot_sequence {
                return Err(invalid_usage("snapshot_sequence"));
            }
            let current = (fact.sequence, fact.minting_rule.code(), fact.fact_id.as_str());
            if previous.is_some_and(|value| value >= current) {
                return Err(invalid_usage("facts"));
            }
            previous = Some(current);
        }
        Ok(())
    }
}

fn invalid_usage(field: &'static str) -> crate::value::BodyError {
    crate::value::BodyError::NotInVocabulary { field }
}

fn usage_identifier(
    value: &str,
    bound: u64,
    field: &'static str,
) -> Result<(), crate::value::BodyError> {
    crate::value::check_str_bound(value, bound, field)?;
    if value.is_empty() || value != value.trim() || value.chars().any(char::is_control) {
        return Err(invalid_usage(field));
    }
    Ok(())
}

fn usage_bytes(
    value: &[u8],
    bound: usize,
    field: &'static str,
) -> Result<(), crate::value::BodyError> {
    if value.len() > bound {
        return Err(crate::value::BodyError::StringTooLong { field, bound });
    }
    Ok(())
}

fn positive_i128(value: &str, field: &'static str) -> Result<i128, crate::value::BodyError> {
    crate::value::check_str_bound(value, 39, field)?;
    let parsed = value.parse::<i128>().map_err(|_| invalid_usage(field))?;
    if parsed <= 0 || parsed.to_string() != value {
        return Err(invalid_usage(field));
    }
    Ok(parsed)
}

/// The `replication_conflicts_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationConflictsRequest {}

impl ReplicationConflictsRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `replication_conflicts_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationConflictsResponse {
    /// The `member` field.
    pub member: String,
    /// The `records` field.
    pub records: Vec<ConflictRecord>,
}

impl ReplicationConflictsResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.member, 128, "member")?;
        crate::value::check_list_bound(&self.records, MAX_LIST_ITEMS, "records")?;
        Ok(())
    }
}

/// The `replication_fenced_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationFencedRequest {}

impl ReplicationFencedRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `replication_fenced_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationFencedResponse {
    /// The `member` field.
    pub member: String,
    /// The `records` field.
    pub records: Vec<FencedRecord>,
}

impl ReplicationFencedResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.member, 128, "member")?;
        crate::value::check_list_bound(&self.records, MAX_LIST_ITEMS, "records")?;
        Ok(())
    }
}

/// The `replication_depositions_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationDepositionsRequest {}

impl ReplicationDepositionsRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `replication_depositions_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationDepositionsResponse {
    /// The `member` field.
    pub member: String,
    /// The `records` field.
    pub records: Vec<DepositionRecord>,
}

impl ReplicationDepositionsResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.member, 128, "member")?;
        crate::value::check_list_bound(&self.records, MAX_LIST_ITEMS, "records")?;
        Ok(())
    }
}

/// The `replication_cursor_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationCursorRequest {}

impl ReplicationCursorRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `replication_cursor_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationCursorResponse {
    /// The `member` field.
    pub member: String,
    /// The `position` field.
    pub position: Option<u64>,
}

impl ReplicationCursorResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.member, 128, "member")?;
        Ok(())
    }
}

/// The `replication_health_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationHealthRequest {}

impl ReplicationHealthRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `replication_health_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationHealthResponse {
    /// The `member` field.
    pub member: String,
    /// The `status` field.
    pub status: ReplicationStatus,
}

impl ReplicationHealthResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.member, 128, "member")?;
        Ok(())
    }
}

/// The `replication_denials_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationDenialsRequest {}

impl ReplicationDenialsRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `replication_denials_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicationDenialsResponse {
    /// The `member` field.
    pub member: String,
    /// The `records` field.
    pub records: Vec<DenialRecord>,
}

impl ReplicationDenialsResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.member, 128, "member")?;
        crate::value::check_list_bound(&self.records, MAX_LIST_ITEMS, "records")?;
        Ok(())
    }
}

/// The `supervision_health_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SupervisionHealthRequest {}

impl SupervisionHealthRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `supervision_health_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SupervisionHealthResponse {
    /// The `state` field.
    pub state: HealthState,
    /// The `sessions` field.
    pub sessions: u32,
    /// The `subscriptions` field.
    pub subscriptions: u32,
    /// The `frames_served` field.
    pub frames_served: u64,
}

impl SupervisionHealthResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `supervision_shutdown_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SupervisionShutdownRequest {
    /// The `reason` field.
    pub reason: ShutdownReason,
}

impl SupervisionShutdownRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `supervision_shutdown_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SupervisionShutdownResponse {
    /// The `state` field.
    pub state: HealthState,
    /// The `in_flight` field.
    pub in_flight: u32,
}

impl SupervisionShutdownResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `schema_declare_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaDeclareRequest {
    /// The `collection` field.
    pub collection: String,
    /// The `schema` field.
    pub schema: Option<TbepValue>,
    /// The `operation_id` field.
    pub operation_id: String,
}

impl SchemaDeclareRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        crate::value::check_str_bound(&self.operation_id, 128, "operation_id")?;
        Ok(())
    }
}

/// The `schema_declare_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaDeclareResponse {
    /// The `enforced_version` field.
    pub enforced_version: Option<u64>,
    /// The `rule_count` field.
    pub rule_count: u32,
}

impl SchemaDeclareResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `schema_status_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaStatusRequest {
    /// The `collection` field.
    pub collection: String,
}

impl SchemaStatusRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        Ok(())
    }
}

/// The `schema_status_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaStatusResponse {
    /// The `enforced_version` field.
    pub enforced_version: Option<u64>,
    /// The `rule_count` field.
    pub rule_count: u32,
}

impl SchemaStatusResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `schema_validate_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaValidateRequest {
    /// The `collection` field.
    pub collection: String,
    /// The `document` field.
    pub document: TbepValue,
}

impl SchemaValidateRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        Ok(())
    }
}

/// The `schema_validate_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaValidateResponse {
    /// The `enforced_version` field.
    pub enforced_version: Option<u64>,
    /// The `valid` field.
    pub valid: bool,
    /// The `violations` field.
    pub violations: Vec<SchemaViolation>,
}

impl SchemaValidateResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(&self.violations, MAX_LIST_ITEMS, "violations")?;
        Ok(())
    }
}

/// The `migration_status_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationStatusRequest {
    /// The `collection` field.
    pub collection: String,
}

impl MigrationStatusRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        Ok(())
    }
}

/// The `migration_status_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationStatusResponse {
    /// The `state` field.
    pub state: MigrationState,
}

impl MigrationStatusResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `migration_dry_run_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationDryRunRequest {
    /// The `contract` field.
    pub contract: SchemaMigrationContract,
}

impl MigrationDryRunRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `migration_dry_run_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationDryRunResponse {
    /// The `plan` field.
    pub plan: MigrationPlan,
}

impl MigrationDryRunResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `migration_apply_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationApplyRequest {
    /// The `contract` field.
    pub contract: SchemaMigrationContract,
    /// The `acknowledge_not_rollbackable` field.
    pub acknowledge_not_rollbackable: bool,
    /// The `operation_id` field.
    pub operation_id: String,
}

impl MigrationApplyRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.operation_id, 128, "operation_id")?;
        Ok(())
    }
}

/// The `migration_apply_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationApplyResponse {
    /// The `receipt` field.
    pub receipt: MigrationReceipt,
}

impl MigrationApplyResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_configure_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierConfigureRequest {
    /// The `policy_id` field.
    pub policy_id: String,
    /// The `version` field.
    pub version: u32,
    /// The `nvme_budget_bytes` field.
    pub nvme_budget_bytes: u64,
    /// The `min_residence_moves` field.
    pub min_residence_moves: u32,
    /// The `promotion_threshold` field.
    pub promotion_threshold: u32,
    /// The `pinned_segment_ids` field.
    pub pinned_segment_ids: Vec<u64>,
}

impl TierConfigureRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.policy_id, 128, "policy_id")?;
        crate::value::check_list_bound(
            &self.pinned_segment_ids,
            MAX_LIST_ITEMS,
            "pinned_segment_ids",
        )?;
        Ok(())
    }
}

/// The `tier_configure_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierConfigureResponse {
    /// The `policy_id` field.
    pub policy_id: String,
    /// The `version` field.
    pub version: u32,
    /// The `paused` field.
    pub paused: bool,
}

impl TierConfigureResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.policy_id, 128, "policy_id")?;
        Ok(())
    }
}

/// The `tier_inspect_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierInspectRequest {}

impl TierInspectRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_inspect_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierInspectResponse {
    /// The `configured` field.
    pub configured: bool,
    /// The `paused` field.
    pub paused: bool,
    /// The `manifest_epoch` field.
    pub manifest_epoch: u64,
    /// The `object_count` field.
    pub object_count: u32,
    /// The `nvme_bytes` field.
    pub nvme_bytes: u64,
}

impl TierInspectResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_dry_run_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierDryRunRequest {}

impl TierDryRunRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_dry_run_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierDryRunResponse {
    /// The `receipt` field.
    pub receipt: TierMoveReceipt,
}

impl TierDryRunResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_explain_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierExplainRequest {}

impl TierExplainRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_explain_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierExplainResponse {
    /// The `receipt` field.
    pub receipt: TierMoveReceipt,
}

impl TierExplainResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_run_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierRunRequest {}

impl TierRunRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_run_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierRunResponse {
    /// The `receipt` field.
    pub receipt: TierMoveReceipt,
}

impl TierRunResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_pause_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierPauseRequest {}

impl TierPauseRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_pause_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierPauseResponse {
    /// The `paused` field.
    pub paused: bool,
}

impl TierPauseResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_resume_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierResumeRequest {}

impl TierResumeRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `tier_resume_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TierResumeResponse {
    /// The `paused` field.
    pub paused: bool,
}

impl TierResumeResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `subscription_batch` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SubscriptionBatch {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `from_sequence` field.
    pub from_sequence: u64,
    /// The `events` field.
    pub events: Vec<ChangeEvent>,
    /// The `next_sequence` field.
    pub next_sequence: u64,
}

impl SubscriptionBatch {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(&self.events, MAX_LIST_ITEMS, "events")?;
        Ok(())
    }
}

/// The `trigger_notification` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct TriggerNotification {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `collection` field.
    pub collection: String,
    /// The `sequence` field.
    pub sequence: u64,
    /// The `operation_id` field.
    pub operation_id: String,
}

impl TriggerNotification {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.collection, 64, "collection")?;
        crate::value::check_str_bound(&self.operation_id, 128, "operation_id")?;
        Ok(())
    }
}

/// The `subscription_closed` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct SubscriptionClosed {
    /// The `subscription_id` field.
    pub subscription_id: u64,
    /// The `reason` field.
    pub reason: SubscriptionCloseReason,
}

impl SubscriptionClosed {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        let _ = self;
        Ok(())
    }
}

/// The `error` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct Error {
    /// The `code` field.
    pub code: ErrorCode,
    /// The `detail` field.
    pub detail: String,
}

impl Error {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_str_bound(&self.detail, 256, "detail")?;
        Ok(())
    }
}

/// The `negotiation_request` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct NegotiationRequest {
    /// The `supported_versions` field.
    pub supported_versions: Vec<u16>,
    /// The `capabilities` field.
    pub capabilities: Vec<String>,
}

impl NegotiationRequest {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(
            &self.supported_versions,
            MAX_LIST_ITEMS,
            "supported_versions",
        )?;
        crate::value::check_list_bound(&self.capabilities, MAX_LIST_ITEMS, "capabilities")?;
        Ok(())
    }
}

/// The `negotiation_response` frame body.
#[derive(Clone, Debug, PartialEq)]
pub struct NegotiationResponse {
    /// The `selected_version` field.
    pub selected_version: u16,
    /// The `accepted_capabilities` field.
    pub accepted_capabilities: Vec<String>,
}

impl NegotiationResponse {
    /// Validates the declared bounds (fail closed).
    ///
    /// # Errors
    ///
    /// Returns the first bounds violation.
    pub fn validate(&self) -> Result<(), crate::value::BodyError> {
        crate::value::check_list_bound(
            &self.accepted_capabilities,
            MAX_LIST_ITEMS,
            "accepted_capabilities",
        )?;
        Ok(())
    }
}

/// One row of the closed frame table: the frame's wire code, kind name,
/// family, and class.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FrameRow {
    /// The frame's wire code.
    pub code: u16,
    /// The frame's kind name (the domain type name).
    pub kind: &'static str,
    /// The frame's family.
    pub family: &'static str,
    /// The frame's class (the budget class of `BUDGETS`).
    pub class: &'static str,
}

/// The closed frame table: every request, response, subscription, and
/// negotiation kind the protocol admits, with its wire code, family, and
/// class. Unknown codes and kinds fail closed on decode.
pub const FRAME_TABLE: &[FrameRow] = &[
    FrameRow { code: 2, kind: "NegotiationRequest", family: "channel", class: "control" },
    FrameRow { code: 3, kind: "NegotiationResponse", family: "channel", class: "control" },
    FrameRow { code: 257, kind: "DocumentReadRequest", family: "documents", class: "operation" },
    FrameRow {
        code: 258,
        kind: "DocumentWriteRequest",
        family: "documents",
        class: "operation_large",
    },
    FrameRow { code: 259, kind: "DocumentDeleteRequest", family: "documents", class: "operation" },
    FrameRow { code: 260, kind: "QueryExecuteRequest", family: "query", class: "operation_large" },
    FrameRow { code: 261, kind: "QueryCancelRequest", family: "query", class: "operation" },
    FrameRow { code: 262, kind: "FamilyReadRequest", family: "families", class: "operation" },
    FrameRow { code: 263, kind: "FamilyWriteRequest", family: "families", class: "operation" },
    FrameRow {
        code: 264,
        kind: "DocumentBatchRequest",
        family: "documents",
        class: "operation_large",
    },
    FrameRow {
        code: 272,
        kind: "BackboneSubscribeRequest",
        family: "backbone",
        class: "operation",
    },
    FrameRow {
        code: 273,
        kind: "BackboneAcknowledgeRequest",
        family: "backbone",
        class: "operation",
    },
    FrameRow {
        code: 274,
        kind: "BackboneUnsubscribeRequest",
        family: "backbone",
        class: "operation",
    },
    FrameRow { code: 288, kind: "SyncSessionSetupRequest", family: "sync", class: "delivery" },
    FrameRow { code: 289, kind: "SyncDeltaPullRequest", family: "sync", class: "delivery" },
    FrameRow { code: 290, kind: "SyncSnapshotPullRequest", family: "sync", class: "delivery" },
    FrameRow { code: 291, kind: "SyncCheckpointAckRequest", family: "sync", class: "delivery" },
    FrameRow { code: 292, kind: "SyncMutationSubmitRequest", family: "sync", class: "delivery" },
    FrameRow { code: 293, kind: "SyncSessionCloseRequest", family: "sync", class: "delivery" },
    FrameRow { code: 304, kind: "TopologyReadRequest", family: "topology", class: "control" },
    FrameRow { code: 305, kind: "AdminStatusRequest", family: "topology", class: "control" },
    FrameRow {
        code: 306,
        kind: "ReplicationConflictsRequest",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 307,
        kind: "ReplicationFencedRequest",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 308,
        kind: "ReplicationDepositionsRequest",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 309,
        kind: "ReplicationCursorRequest",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 310,
        kind: "ReplicationHealthRequest",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 311,
        kind: "ReplicationDenialsRequest",
        family: "replication",
        class: "observation",
    },
    FrameRow { code: 320, kind: "ChannelBindRequest", family: "supervision", class: "control" },
    FrameRow {
        code: 321,
        kind: "SupervisionHealthRequest",
        family: "supervision",
        class: "control",
    },
    FrameRow {
        code: 322,
        kind: "SupervisionShutdownRequest",
        family: "supervision",
        class: "control",
    },
    FrameRow { code: 336, kind: "SchemaDeclareRequest", family: "schemas", class: "schema" },
    FrameRow { code: 337, kind: "SchemaStatusRequest", family: "schemas", class: "schema" },
    FrameRow { code: 338, kind: "SchemaValidateRequest", family: "schemas", class: "schema" },
    FrameRow { code: 339, kind: "MigrationStatusRequest", family: "schemas", class: "schema" },
    FrameRow { code: 340, kind: "MigrationDryRunRequest", family: "schemas", class: "schema" },
    FrameRow { code: 341, kind: "MigrationApplyRequest", family: "schemas", class: "schema" },
    FrameRow { code: 342, kind: "TierConfigureRequest", family: "tiers", class: "control" },
    FrameRow { code: 343, kind: "TierInspectRequest", family: "tiers", class: "control" },
    FrameRow { code: 344, kind: "TierDryRunRequest", family: "tiers", class: "operation" },
    FrameRow { code: 345, kind: "TierExplainRequest", family: "tiers", class: "operation" },
    FrameRow { code: 346, kind: "TierRunRequest", family: "tiers", class: "operation" },
    FrameRow { code: 347, kind: "TierPauseRequest", family: "tiers", class: "control" },
    FrameRow { code: 348, kind: "TierResumeRequest", family: "tiers", class: "control" },
    FrameRow { code: 513, kind: "DocumentReadResponse", family: "documents", class: "operation" },
    FrameRow {
        code: 514,
        kind: "DocumentWriteResponse",
        family: "documents",
        class: "operation_large",
    },
    FrameRow { code: 515, kind: "DocumentDeleteResponse", family: "documents", class: "operation" },
    FrameRow { code: 516, kind: "QueryExecuteResponse", family: "query", class: "operation_large" },
    FrameRow { code: 517, kind: "QueryCancelResponse", family: "query", class: "operation" },
    FrameRow { code: 518, kind: "FamilyReadResponse", family: "families", class: "operation" },
    FrameRow { code: 519, kind: "FamilyWriteResponse", family: "families", class: "operation" },
    FrameRow {
        code: 528,
        kind: "BackboneSubscribeResponse",
        family: "backbone",
        class: "operation",
    },
    FrameRow {
        code: 529,
        kind: "BackboneAcknowledgeResponse",
        family: "backbone",
        class: "operation",
    },
    FrameRow {
        code: 530,
        kind: "BackboneUnsubscribeResponse",
        family: "backbone",
        class: "operation",
    },
    FrameRow { code: 544, kind: "SyncSessionSetupResponse", family: "sync", class: "delivery" },
    FrameRow { code: 545, kind: "SyncDeltaPullResponse", family: "sync", class: "delivery" },
    FrameRow { code: 546, kind: "SyncSnapshotPullResponse", family: "sync", class: "delivery" },
    FrameRow { code: 547, kind: "SyncCheckpointAckResponse", family: "sync", class: "delivery" },
    FrameRow { code: 548, kind: "SyncMutationSubmitResponse", family: "sync", class: "delivery" },
    FrameRow { code: 549, kind: "SyncSessionCloseResponse", family: "sync", class: "delivery" },
    FrameRow { code: 560, kind: "TopologyReadResponse", family: "topology", class: "control" },
    FrameRow { code: 561, kind: "AdminStatusResponse", family: "topology", class: "control" },
    FrameRow {
        code: 562,
        kind: "ReplicationConflictsResponse",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 563,
        kind: "ReplicationFencedResponse",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 564,
        kind: "ReplicationDepositionsResponse",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 565,
        kind: "ReplicationCursorResponse",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 566,
        kind: "ReplicationHealthResponse",
        family: "replication",
        class: "observation",
    },
    FrameRow {
        code: 567,
        kind: "ReplicationDenialsResponse",
        family: "replication",
        class: "observation",
    },
    FrameRow { code: 576, kind: "ChannelBindResponse", family: "supervision", class: "control" },
    FrameRow {
        code: 577,
        kind: "SupervisionHealthResponse",
        family: "supervision",
        class: "control",
    },
    FrameRow {
        code: 578,
        kind: "SupervisionShutdownResponse",
        family: "supervision",
        class: "control",
    },
    FrameRow { code: 592, kind: "SchemaDeclareResponse", family: "schemas", class: "schema" },
    FrameRow { code: 593, kind: "SchemaStatusResponse", family: "schemas", class: "schema" },
    FrameRow { code: 594, kind: "SchemaValidateResponse", family: "schemas", class: "schema" },
    FrameRow { code: 595, kind: "MigrationStatusResponse", family: "schemas", class: "schema" },
    FrameRow { code: 596, kind: "MigrationDryRunResponse", family: "schemas", class: "schema" },
    FrameRow { code: 597, kind: "MigrationApplyResponse", family: "schemas", class: "schema" },
    FrameRow { code: 598, kind: "TierConfigureResponse", family: "tiers", class: "control" },
    FrameRow { code: 599, kind: "TierInspectResponse", family: "tiers", class: "control" },
    FrameRow { code: 600, kind: "TierDryRunResponse", family: "tiers", class: "operation" },
    FrameRow { code: 601, kind: "TierExplainResponse", family: "tiers", class: "operation" },
    FrameRow { code: 602, kind: "TierRunResponse", family: "tiers", class: "operation" },
    FrameRow { code: 603, kind: "TierPauseResponse", family: "tiers", class: "control" },
    FrameRow { code: 604, kind: "TierResumeResponse", family: "tiers", class: "control" },
    FrameRow {
        code: 605,
        kind: "DocumentBatchResponse",
        family: "documents",
        class: "operation_large",
    },
    FrameRow { code: 769, kind: "SubscriptionBatch", family: "backbone", class: "subscription" },
    FrameRow { code: 770, kind: "TriggerNotification", family: "backbone", class: "trigger" },
    FrameRow { code: 771, kind: "SubscriptionClosed", family: "backbone", class: "control" },
];

/// Looks one frame row up by wire code.
///
/// Unknown codes are `None` and fail closed at the caller.
#[must_use]
pub const fn frame_row_for_code(code: u16) -> Option<&'static FrameRow> {
    let mut index = 0;
    while index < FRAME_TABLE.len() {
        let row = &FRAME_TABLE[index];
        if row.code == code {
            return Some(row);
        }
        index += 1;
    }
    None
}

/// Looks one frame row up by kind name.
///
/// Unknown kinds are `None` and fail closed at the caller.
#[must_use]
pub fn frame_row_for_kind(kind: &str) -> Option<&'static FrameRow> {
    FRAME_TABLE.iter().find(|row| row.kind == kind)
}

/// The closed family vocabulary of the frame table.
#[must_use]
pub fn families() -> Vec<&'static str> {
    let mut seen: Vec<&'static str> = Vec::new();
    for row in FRAME_TABLE {
        if !seen.contains(&row.family) {
            seen.push(row.family);
        }
    }
    seen
}

/// The written bytes-on-wire budget for one frame class.
///
/// Unknown classes are `None` and fail closed at the caller.
#[must_use]
pub fn budget_for_class(class: &str) -> Option<usize> {
    let mut index = 0;
    while index < BUDGETS.len() {
        let (name, budget) = BUDGETS[index];
        if name == class {
            return Some(budget);
        }
        index += 1;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_codes_are_unique() {
        for (index, row) in FRAME_TABLE.iter().enumerate() {
            for (other_index, other) in FRAME_TABLE.iter().enumerate() {
                if index != other_index {
                    assert_ne!(row.code, other.code, "code {} is not unique", row.code);
                }
            }
        }
    }

    #[test]
    fn frame_kinds_are_unique() {
        let mut kinds: Vec<&str> = FRAME_TABLE.iter().map(|row| row.kind).collect();
        kinds.sort_unstable();
        let count = kinds.len();
        kinds.dedup();
        assert_eq!(kinds.len(), count, "frame kinds are not unique");
    }

    #[test]
    fn every_frame_class_has_a_budget() {
        for row in FRAME_TABLE {
            assert!(
                budget_for_class(row.class).is_some(),
                "class {} of kind {} has no budget",
                row.class,
                row.kind
            );
        }
    }

    #[test]
    fn every_frame_kind_has_a_domain_type_name() {
        // The kind is the domain type name; the closed set of families
        // pins the serving seams that can carry each kind.
        for row in FRAME_TABLE {
            assert!(
                !row.kind.is_empty() && row.kind.chars().next().is_some_and(char::is_uppercase),
                "kind {} is not a domain type name",
                row.kind
            );
        }
    }

    #[test]
    fn lookups_fail_closed_for_unknown_identities() {
        assert!(frame_row_for_code(1).is_none());
        assert!(frame_row_for_code(u16::MAX).is_none());
        assert!(frame_row_for_kind("NoSuchFrameKind").is_none());
        assert!(budget_for_class("no_such_class").is_none());
    }

    #[test]
    fn known_lookups_resolve() {
        let negotiation = frame_row_for_code(2).expect("code 2 resolves");
        assert_eq!(negotiation.kind, "NegotiationRequest");
        assert_eq!(negotiation.family, "channel");
        let closed = frame_row_for_kind("SubscriptionClosed").expect("kind resolves");
        assert_eq!(closed.code, 771);
        assert_eq!(budget_for_class("control"), Some(256));
        assert_eq!(budget_for_class("operation_large"), Some(65_536));
        assert!(families().contains(&"documents"));
    }
}
