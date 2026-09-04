//! The engine-side session and unit-of-work contract (GAP-D01).
//!
//! One [`Session`] is one unit of work bound to one [`Engine`], one
//! Platform-minted [`TenantScope`], and one
//! [`SessionOptions`] set. Stores and deletes stay deferred until
//! [`Session::save_changes`], loaded documents are pinned by an identity
//! map, [`Session::what_changed`] reports field-level old/new values
//! against the pinned originals, lazy reads batch, and deferred typed
//! family commands commit atomically with the documents — the V1
//! `IDocumentSession` observable contract mapped onto the engine seam.
//!
//! The ownership decision behind this engine-side placement is
//! `docs/sessions/gapd01-ownership-decision.md`; the rule-by-rule V1
//! mapping is `docs/sessions/session-contract.md`.

#![allow(clippy::all, reason = "session contract lint debt is tracked while the API stabilizes")]
#![allow(
    clippy::pedantic,
    reason = "session contract lint debt is tracked while the API stabilizes"
)]

use std::{
    collections::{BTreeMap, BTreeSet},
    fmt,
    sync::atomic::{AtomicU64, Ordering},
    thread,
    time::{Duration, Instant},
};

use crate::engine::{CommitOutcome, Engine, EngineError};
use crate::families::{
    FamilyError, FamilyReadContext, FamilyReadRequest, FamilyReadResult, FamilyWriteRequest,
    IdentityCommand, IdentityRead, IdentityWrite,
};
use crate::streaming::{
    DocumentStreamSelector, StreamBatch, StreamCursor, StreamError, StreamItem, StreamSource,
};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, ReadSnapshot, RegionId, ReplicationClockStamp, StorageSequence,
    TenantScope,
};

#[cfg(test)]
mod tests;

/// The liveness bound every session-built family request carries.
const FAMILY_DEADLINE: Duration = Duration::from_secs(30);

/// The batch limit one `load_starting_with` page pull uses per stream batch.
const PAGE_BATCH_LIMIT: u32 = 256;

/// The prefix of the identity-family name one collection's generated ids
/// allocate from; cross-client `HiLo` block distribution is GAP-F9's.
const IDENTITY_NAME_PREFIX: &str = "documents/";

/// The maximum encoded session token accepted by the public contract.
pub const MAX_SESSION_TOKEN_BYTES: usize = 16 * 1024;

/// The current opaque session-token encoding version.
pub const SESSION_TOKEN_FORMAT_VERSION: u8 = 1;

/// The bounded default wait for a causal read to catch up.
pub const DEFAULT_SESSION_WAIT: Duration = Duration::from_secs(2);
/// Hard ceiling for one causal-read wait, preventing an unbounded caller
/// timeout from pinning a request thread indefinitely.
pub const MAX_SESSION_WAIT: Duration = Duration::from_secs(30);

const SESSION_TOKEN_MAGIC: &[u8; 4] = b"TSS1";
const SESSION_TOKEN_CHECKSUM_BYTES: usize = 32;

/// A non-zero database incarnation. Session tokens cannot cross an epoch:
/// accepting one after restore or replacement could expose an unrelated
/// database state as if it were causally newer.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct SessionEpoch(u64);

impl SessionEpoch {
    /// Creates an epoch. Zero is reserved for an absent or unvalidated epoch.
    pub const fn new(value: u64) -> Result<Self, SessionTokenError> {
        if value == 0 { Err(SessionTokenError::ZeroEpoch) } else { Ok(Self(value)) }
    }

    /// Returns the wire epoch value.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// Errors returned while constructing or decoding the opaque session token.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SessionTokenError {
    /// The encoded token is empty, truncated, or has trailing bytes.
    MalformedEncoding,
    /// The token uses a format version this member does not understand.
    UnsupportedVersion(u8),
    /// The encoded token exceeds the public 16 KiB bound.
    TooLarge,
    /// The checksum does not match the token body.
    ChecksumMismatch,
    /// The body is not in canonical origin order or contains a duplicate.
    NonCanonical,
    /// An epoch of zero is not a valid database incarnation.
    ZeroEpoch,
    /// A frontier sequence of zero is reserved for an absent origin.
    ZeroSequence,
    /// A frontier origin could not be validated as a logical Region id.
    InvalidOrigin,
    /// The supplied token and frontier belong to different epochs.
    EpochMismatch {
        /// The epoch the caller expected.
        expected: SessionEpoch,
        /// The epoch found in the supplied token or frontier.
        observed: SessionEpoch,
    },
    /// Adding an origin would make the encoded token exceed the public bound.
    OriginLimitExceeded,
}

impl fmt::Display for SessionTokenError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MalformedEncoding => formatter.write_str("malformed session token encoding"),
            Self::UnsupportedVersion(version) => {
                write!(formatter, "unsupported session token version {version}")
            }
            Self::TooLarge => formatter.write_str("session token exceeds 16 KiB"),
            Self::ChecksumMismatch => formatter.write_str("session token checksum mismatch"),
            Self::NonCanonical => formatter.write_str("non-canonical session token encoding"),
            Self::ZeroEpoch => formatter.write_str("session epoch zero is reserved"),
            Self::ZeroSequence => formatter.write_str("frontier sequence zero is reserved"),
            Self::InvalidOrigin => formatter.write_str("invalid frontier origin"),
            Self::EpochMismatch { expected, observed } => {
                write!(
                    formatter,
                    "session epoch mismatch (expected {}, observed {})",
                    expected.0, observed.0
                )
            }
            Self::OriginLimitExceeded => {
                formatter.write_str("session token frontier exceeds the 16 KiB origin bound")
            }
        }
    }
}

impl std::error::Error for SessionTokenError {}

/// A decoded causal frontier. The token itself remains opaque to callers;
/// this value is the validated member-side representation used for checks.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SessionFrontier {
    epoch: SessionEpoch,
    origins: BTreeMap<RegionId, u64>,
    last_write: ReplicationClockStamp,
}

impl SessionFrontier {
    /// Creates an empty frontier for one database epoch.
    pub const fn new(epoch: SessionEpoch) -> Self {
        Self { epoch, origins: BTreeMap::new(), last_write: ReplicationClockStamp::new(0, 0) }
    }

    /// Creates a frontier from validated entries, rejecting zero sequences
    /// and duplicate origins. The map is canonicalized by `RegionId` order.
    pub fn try_from_entries(
        epoch: SessionEpoch,
        entries: impl IntoIterator<Item = (RegionId, u64)>,
        last_write: ReplicationClockStamp,
    ) -> Result<Self, SessionTokenError> {
        let mut frontier = Self::new(epoch);
        for (origin, sequence) in entries {
            if frontier.origins.contains_key(&origin) {
                return Err(SessionTokenError::NonCanonical);
            }
            frontier.advance(origin, sequence)?;
        }
        Ok(Self { last_write, ..frontier })
    }

    /// Returns the database epoch.
    #[must_use]
    pub const fn epoch(&self) -> SessionEpoch {
        self.epoch
    }

    /// Returns the HLC stamp of the client's latest write.
    #[must_use]
    pub const fn last_write(&self) -> ReplicationClockStamp {
        self.last_write
    }

    /// Returns the sequence observed for `origin`, or zero if absent.
    #[must_use]
    pub fn sequence(&self, origin: &RegionId) -> u64 {
        self.origins.get(origin).copied().unwrap_or(0)
    }

    /// Iterates over origins in canonical order.
    pub fn entries(&self) -> impl Iterator<Item = (&RegionId, u64)> + '_ {
        self.origins.iter().map(|(origin, sequence)| (origin, *sequence))
    }

    /// Advances one origin without allowing a causal regression.
    pub fn advance(&mut self, origin: RegionId, sequence: u64) -> Result<bool, SessionTokenError> {
        if sequence == 0 {
            return Err(SessionTokenError::ZeroSequence);
        }
        let is_new = !self.origins.contains_key(&origin);
        if is_new {
            // Keep the infallible `encode` API safe: a token constructed via
            // repeated `advance` calls must never truncate the u16 origin
            // count or produce a wire value that decode rejects as too large.
            let origin_bytes = origin.as_str().len();
            let projected = 4_usize
                .saturating_add(1)
                .saturating_add(8)
                .saturating_add(8)
                .saturating_add(8)
                .saturating_add(2)
                .saturating_add(2)
                .saturating_add(origin_bytes)
                .saturating_add(8)
                .saturating_add(
                    self.origins
                        .keys()
                        .map(|existing| 2 + existing.as_str().len() + 8)
                        .sum::<usize>(),
                )
                .saturating_add(SESSION_TOKEN_CHECKSUM_BYTES);
            if projected > MAX_SESSION_TOKEN_BYTES || self.origins.len() >= u16::MAX as usize {
                return Err(SessionTokenError::OriginLimitExceeded);
            }
        }
        let entry = self.origins.entry(origin).or_insert(0);
        if sequence > *entry {
            *entry = sequence;
            Ok(true)
        } else {
            Ok(false)
        }
    }

    /// Advances the HLC monotonically.
    pub fn observe_write(&mut self, stamp: ReplicationClockStamp) {
        if stamp > self.last_write {
            self.last_write = stamp;
        }
    }

    /// Returns whether this frontier covers every dependency in `required`.
    #[must_use]
    pub fn covers(&self, required: &Self) -> bool {
        self.epoch == required.epoch
            && required.origins.iter().all(|(origin, sequence)| self.sequence(origin) >= *sequence)
    }

    /// Merges a newer observation from the same database epoch.
    pub fn merge(&mut self, other: &Self) -> Result<bool, SessionTokenError> {
        if self.epoch != other.epoch {
            return Err(SessionTokenError::EpochMismatch {
                expected: self.epoch,
                observed: other.epoch,
            });
        }
        let mut changed = false;
        for (origin, sequence) in &other.origins {
            changed |= self.advance(origin.clone(), *sequence)?;
        }
        if other.last_write > self.last_write {
            self.last_write = other.last_write;
            changed = true;
        }
        Ok(changed)
    }
}

/// An opaque, versioned causal session token carried by SDK requests.
///
/// Its bytes are intentionally inaccessible except through `encode` and
/// `decode`; callers may pass them between clients without interpreting
/// topology or causal metadata. The member validates and interprets it.
#[derive(Clone, Eq, PartialEq)]
pub struct SessionToken {
    frontier: SessionFrontier,
}

impl fmt::Debug for SessionToken {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("SessionToken(opaque)")
    }
}

impl SessionToken {
    /// Creates an empty token for an epoch.
    #[must_use]
    pub const fn new(epoch: SessionEpoch) -> Self {
        Self { frontier: SessionFrontier::new(epoch) }
    }

    /// Creates an empty token from a raw non-zero epoch.
    pub fn for_epoch(epoch: u64) -> Result<Self, SessionTokenError> {
        Ok(Self::new(SessionEpoch::new(epoch)?))
    }

    /// Wraps an already validated frontier.
    #[must_use]
    pub const fn from_frontier(frontier: SessionFrontier) -> Self {
        Self { frontier }
    }

    /// Returns the epoch after decoding and validation.
    #[must_use]
    pub const fn epoch(&self) -> SessionEpoch {
        self.frontier.epoch()
    }

    /// Returns a validated member-side frontier view.
    #[must_use]
    pub const fn frontier(&self) -> &SessionFrontier {
        &self.frontier
    }

    /// Advances one observed origin monotonically.
    pub fn advance(&mut self, origin: RegionId, sequence: u64) -> Result<bool, SessionTokenError> {
        self.frontier.advance(origin, sequence)
    }

    /// Records the latest write HLC monotonically.
    pub fn observe_write(&mut self, stamp: ReplicationClockStamp) {
        self.frontier.observe_write(stamp);
    }

    /// Merges a response observation, refusing cross-epoch tokens.
    pub fn merge(&mut self, other: &Self) -> Result<bool, SessionTokenError> {
        self.frontier.merge(&other.frontier)
    }

    /// Returns whether this token's frontier covers `required`.
    #[must_use]
    pub fn covers(&self, required: &SessionFrontier) -> bool {
        self.frontier.covers(required)
    }

    /// Encodes the canonical opaque token, including a tamper-detecting hash.
    pub fn encode(&self) -> Vec<u8> {
        let mut body = Vec::with_capacity(64);
        body.extend_from_slice(SESSION_TOKEN_MAGIC);
        body.push(SESSION_TOKEN_FORMAT_VERSION);
        body.extend_from_slice(&self.epoch().get().to_be_bytes());
        body.extend_from_slice(&self.frontier.last_write().ticks().to_be_bytes());
        body.extend_from_slice(&self.frontier.last_write().counter().to_be_bytes());
        body.extend_from_slice(&(self.frontier.origins.len() as u16).to_be_bytes());
        for (origin, sequence) in self.frontier.entries() {
            let bytes = origin.as_str().as_bytes();
            body.extend_from_slice(&(bytes.len() as u16).to_be_bytes());
            body.extend_from_slice(bytes);
            body.extend_from_slice(&sequence.to_be_bytes());
        }
        let checksum = blake3::hash(&body);
        let mut encoded = body;
        encoded.extend_from_slice(checksum.as_bytes());
        encoded
    }

    /// Alias for `encode`, useful at protocol boundaries.
    pub fn encoded(&self) -> Vec<u8> {
        self.encode()
    }

    /// Decodes and validates one exact canonical token.
    pub fn decode(bytes: &[u8]) -> Result<Self, SessionTokenError> {
        if bytes.len() > MAX_SESSION_TOKEN_BYTES {
            return Err(SessionTokenError::TooLarge);
        }
        if bytes.len() < 4 + 1 + 8 + 8 + 8 + 2 + SESSION_TOKEN_CHECKSUM_BYTES {
            return Err(SessionTokenError::MalformedEncoding);
        }
        let checksum_at = bytes.len() - SESSION_TOKEN_CHECKSUM_BYTES;
        if blake3::hash(&bytes[..checksum_at]).as_bytes() != &bytes[checksum_at..] {
            return Err(SessionTokenError::ChecksumMismatch);
        }
        let mut reader = TokenReader::new(&bytes[..checksum_at]);
        if reader.take(4)? != SESSION_TOKEN_MAGIC {
            return Err(SessionTokenError::MalformedEncoding);
        }
        let version = reader.byte()?;
        if version != SESSION_TOKEN_FORMAT_VERSION {
            return Err(SessionTokenError::UnsupportedVersion(version));
        }
        let epoch = SessionEpoch::new(reader.u64()?)?;
        let last_write = ReplicationClockStamp::new(reader.u64()?, reader.u64()?);
        let count = usize::from(reader.u16()?);
        let mut entries = Vec::with_capacity(count);
        for _ in 0..count {
            let length = usize::from(reader.u16()?);
            let origin = std::str::from_utf8(reader.take(length)?)
                .map_err(|_| SessionTokenError::InvalidOrigin)?;
            let origin = RegionId::try_new(origin).map_err(|_| SessionTokenError::InvalidOrigin)?;
            let sequence = reader.u64()?;
            entries.push((origin, sequence));
        }
        if !reader.finished() {
            return Err(SessionTokenError::MalformedEncoding);
        }
        let token =
            Self { frontier: SessionFrontier::try_from_entries(epoch, entries, last_write)? };
        if token.encode() != bytes {
            return Err(SessionTokenError::NonCanonical);
        }
        Ok(token)
    }

    /// Alias for `decode` at a byte-oriented protocol boundary.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, SessionTokenError> {
        Self::decode(bytes)
    }

    /// Applies the session guarantee and returns a typed stale result when
    /// the bounded wait is exhausted. Eventual reads deliberately skip this
    /// check. No variant offers linearizability.
    pub fn wait_for<F>(
        &self,
        policy: ReadConsistency,
        timeout: Duration,
        safe_region: RegionId,
        mut applied: F,
    ) -> Result<(), SessionReadError>
    where
        F: FnMut() -> Result<SessionFrontier, SessionTokenError>,
    {
        if policy == ReadConsistency::Eventual {
            return Ok(());
        }
        let started = Instant::now();
        let timeout = timeout.min(MAX_SESSION_WAIT);
        loop {
            let current = applied().map_err(SessionReadError::Token)?;
            if current.epoch() != self.epoch() {
                return Err(SessionReadError::Token(SessionTokenError::EpochMismatch {
                    expected: self.epoch(),
                    observed: current.epoch(),
                }));
            }
            if current.covers(&self.frontier) {
                return Ok(());
            }
            if timeout.is_zero() || started.elapsed() >= timeout {
                return Err(SessionReadError::StaleReplica(StaleReplica::new(safe_region)));
            }
            // A causal frontier may take a short while to arrive. Sleeping a
            // bounded slice keeps this synchronous seam CPU-safe under a
            // partition while preserving prompt timeout/cancellation errors
            // from the applied callback.
            thread::sleep(Duration::from_millis(1).min(timeout.saturating_sub(started.elapsed())));
        }
    }
}

/// A per-request causal read mode. `Default` is read-your-writes plus
/// monotonic reads; the only weaker mode is `Eventual`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum ReadConsistency {
    /// Read-your-writes and monotonic reads (the default).
    #[default]
    Default,
    /// Serve the local state without waiting for a session frontier.
    Eventual,
    /// Require the session frontier before serving, including observations
    /// learned from prior reads.
    WritesFollowReads,
}

impl ReadConsistency {
    /// Returns whether this mode intentionally permits a stale local read.
    #[must_use]
    pub const fn is_eventual(self) -> bool {
        matches!(self, Self::Eventual)
    }

    /// Returns whether this mode checks the supplied session frontier.
    #[must_use]
    pub const fn requires_frontier(self) -> bool {
        !self.is_eventual()
    }
}

/// A validated per-request causal-read policy.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SessionReadPolicy {
    consistency: ReadConsistency,
    wait_timeout: Duration,
}

impl SessionReadPolicy {
    /// Creates a policy. A zero timeout is valid and means "check once, then
    /// return `StaleReplica`"; it is useful for non-blocking members.
    #[must_use]
    pub fn new(consistency: ReadConsistency, wait_timeout: Duration) -> Self {
        let wait_timeout =
            if wait_timeout > MAX_SESSION_WAIT { MAX_SESSION_WAIT } else { wait_timeout };
        Self { consistency, wait_timeout }
    }

    /// Returns the selected consistency mode.
    #[must_use]
    pub const fn consistency(self) -> ReadConsistency {
        self.consistency
    }

    /// Returns the bounded wait.
    #[must_use]
    pub const fn wait_timeout(self) -> Duration {
        self.wait_timeout
    }

    /// Enforces this policy against successive applied-frontier observations.
    pub fn enforce<F>(
        self,
        token: &SessionToken,
        safe_region: RegionId,
        applied: F,
    ) -> Result<(), SessionReadError>
    where
        F: FnMut() -> Result<SessionFrontier, SessionTokenError>,
    {
        token.wait_for(self.consistency, self.wait_timeout, safe_region, applied)
    }
}

impl Default for SessionReadPolicy {
    fn default() -> Self {
        Self::new(ReadConsistency::Default, DEFAULT_SESSION_WAIT)
    }
}

/// Compatibility aliases for SDK and server terminology.
pub type SessionConsistency = ReadConsistency;
/// Compatibility spelling for the SDK's read-guarantee terminology.
pub type ReadGuarantee = ReadConsistency;

/// A typed stale-replica outcome that names a Region safe for retry.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StaleReplica {
    safe_region: RegionId,
}

impl StaleReplica {
    /// Creates a stale outcome naming the Region that can serve the token.
    #[must_use]
    pub const fn new(safe_region: RegionId) -> Self {
        Self { safe_region }
    }

    /// Returns the safe retry Region.
    #[must_use]
    pub const fn safe_region(&self) -> &RegionId {
        &self.safe_region
    }

    /// Alias used by routing adapters.
    #[must_use]
    pub const fn region(&self) -> &RegionId {
        &self.safe_region
    }
}

/// Errors from applying a session read guarantee.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SessionReadError {
    /// The member could not validate its applied frontier or token.
    Token(SessionTokenError),
    /// The bounded wait expired; retry at this safe Region.
    StaleReplica(StaleReplica),
}

/// A small byte reader used only by the strict session-token codec.
struct TokenReader<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> TokenReader<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }
    fn take(&mut self, count: usize) -> Result<&'a [u8], SessionTokenError> {
        let end = self.offset.checked_add(count).ok_or(SessionTokenError::MalformedEncoding)?;
        let value = self.bytes.get(self.offset..end).ok_or(SessionTokenError::MalformedEncoding)?;
        self.offset = end;
        Ok(value)
    }
    fn byte(&mut self) -> Result<u8, SessionTokenError> {
        Ok(*self.take(1)?.first().expect("one byte"))
    }
    fn u16(&mut self) -> Result<u16, SessionTokenError> {
        Ok(u16::from_be_bytes(self.take(2)?.try_into().expect("two bytes")))
    }
    fn u64(&mut self) -> Result<u64, SessionTokenError> {
        Ok(u64::from_be_bytes(self.take(8)?.try_into().expect("eight bytes")))
    }
    fn finished(&self) -> bool {
        self.offset == self.bytes.len()
    }
}

/// How a session treats loaded documents (V1 `SessionOptions.NoTracking`).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SessionTracking {
    /// Loads pin documents in the identity map (the default).
    Tracked,
    /// Loads answer without tracking; every write path refuses (V1 throws
    /// on `Store` and `SaveChanges` in this mode).
    NoTracking,
}

/// The transaction mode one save runs under (V1 `TransactionMode`).
///
/// `ClusterWide` fails closed at open: no V2 cluster transaction exists
/// (SH-05 owns it), so accepting the mode and failing at save would let a
/// session accumulate work that can never commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SessionTransactionMode {
    /// One atomic single-node commit per save (the default).
    SingleNode,
    /// Cluster-wide transactions; refused at open until SH-05 lands.
    ClusterWide,
}

/// Options for opening one [`Session`] (V1 `SessionOptions`).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SessionOptions {
    tracking: SessionTracking,
    transaction_mode: SessionTransactionMode,
    durability: LocalDurability,
    consistency: ReadConsistency,
    wait_timeout: Duration,
    session_token: Option<SessionToken>,
}

impl SessionOptions {
    /// Creates the default options: tracked, single-node, synced saves.
    #[must_use]
    pub const fn new() -> Self {
        Self {
            tracking: SessionTracking::Tracked,
            transaction_mode: SessionTransactionMode::SingleNode,
            durability: LocalDurability::Synced,
            consistency: ReadConsistency::Default,
            wait_timeout: DEFAULT_SESSION_WAIT,
            session_token: None,
        }
    }

    /// Selects no-tracking reads.
    #[must_use]
    pub const fn no_tracking(mut self) -> Self {
        self.tracking = SessionTracking::NoTracking;
        self
    }

    /// Selects the transaction mode every save runs under.
    #[must_use]
    pub const fn cluster_wide(mut self) -> Self {
        self.transaction_mode = SessionTransactionMode::ClusterWide;
        self
    }

    /// Selects the local durability point every save commits to.
    #[must_use]
    pub const fn durability(mut self, durability: LocalDurability) -> Self {
        self.durability = durability;
        self
    }

    /// Selects the per-read causal guarantee. The default combines
    /// read-your-writes and monotonic reads; `Eventual` is an explicit opt-out.
    #[must_use]
    pub const fn consistency(mut self, consistency: ReadConsistency) -> Self {
        self.consistency = consistency;
        self
    }

    /// Selects eventual reads for this session.
    #[must_use]
    pub const fn eventual(self) -> Self {
        self.consistency(ReadConsistency::Eventual)
    }

    /// Selects writes-follow-reads for this session.
    #[must_use]
    pub const fn writes_follow_reads(self) -> Self {
        self.consistency(ReadConsistency::WritesFollowReads)
    }

    /// Sets the maximum bounded wait before a stale replica is returned.
    #[must_use]
    pub const fn wait_timeout(mut self, timeout: Duration) -> Self {
        self.wait_timeout = timeout;
        self
    }

    /// Continues an existing opaque causal session token.
    #[must_use]
    pub fn with_session_token(mut self, token: SessionToken) -> Self {
        self.session_token = Some(token);
        self
    }

    /// Returns the tracking mode.
    #[must_use]
    pub const fn tracking(&self) -> SessionTracking {
        self.tracking
    }

    /// Returns the transaction mode.
    #[must_use]
    pub const fn transaction_mode(&self) -> SessionTransactionMode {
        self.transaction_mode
    }

    /// Returns the save durability point.
    #[must_use]
    pub const fn save_durability(&self) -> LocalDurability {
        self.durability
    }

    /// Returns the default read consistency for this session.
    #[must_use]
    pub const fn consistency_mode(&self) -> ReadConsistency {
        self.consistency
    }

    /// Alias for [`Self::consistency_mode`].
    #[must_use]
    pub const fn read_consistency(&self) -> ReadConsistency {
        self.consistency
    }

    /// Returns the bounded causal wait.
    #[must_use]
    pub const fn wait_timeout_duration(&self) -> Duration {
        self.wait_timeout
    }

    /// Returns the token supplied when this session is opened, if any.
    #[must_use]
    pub const fn supplied_session_token(&self) -> Option<&SessionToken> {
        self.session_token.as_ref()
    }
}

impl Default for SessionOptions {
    fn default() -> Self {
        Self::new()
    }
}

/// A typed session failure. No path, payload, or transport detail crosses
/// this seam.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SessionError {
    /// The scope names another Engine.
    ScopeMismatch,
    /// The requested mode has no V2 implementation (SH-05 owns cluster
    /// transactions), so the session refuses to open rather than accept
    /// work that can never commit.
    ClusterWideUnavailable,
    /// A write path ran on a no-tracking session (V1 throws the same
    /// refusal set: `Store` and `SaveChanges`).
    NoTrackingRefused,
    /// The named document is not tracked by this session.
    NotTracked {
        /// The requested document identity.
        document: DocumentId,
    },
    /// A deferred command guards a document this save also deletes — V1
    /// refuses the whole save (`ThrowInvalidDeletedDocumentWithDeferredCommand`).
    DeferredCommandOnDeletedDocument {
        /// The contested document identity.
        document: DocumentId,
    },
    /// A lifecycle hook vetoed the save; nothing was staged or committed.
    HookVetoed {
        /// The veto reason the hook supplied.
        reason: String,
    },
    /// The Engine refused a session operation.
    Engine(EngineError),
    /// A deferred family command was refused by the family seam.
    Family(FamilyError),
    /// The stream seam refused a session stream operation.
    Stream(StreamError),
    /// A supplied opaque session token failed strict validation.
    SessionToken(SessionTokenError),
    /// The member was behind the token after the bounded causal wait.
    StaleReplica(StaleReplica),
    /// A durable commit completed but its causal write frontier could not be
    /// observed advancing. The save is reported as failed closed because a
    /// successful save must never lose read-your-writes state.
    WriteFrontierUnavailable,
}

impl From<EngineError> for SessionError {
    fn from(error: EngineError) -> Self {
        match error {
            EngineError::ScopeMismatch => Self::ScopeMismatch,
            other => Self::Engine(other),
        }
    }
}

impl From<FamilyError> for SessionError {
    fn from(error: FamilyError) -> Self {
        Self::Family(error)
    }
}

impl From<StreamError> for SessionError {
    fn from(error: StreamError) -> Self {
        Self::Stream(error)
    }
}

impl From<SessionTokenError> for SessionError {
    fn from(error: SessionTokenError) -> Self {
        Self::SessionToken(error)
    }
}

impl From<SessionReadError> for SessionError {
    fn from(error: SessionReadError) -> Self {
        match error {
            SessionReadError::Token(error) => Self::SessionToken(error),
            SessionReadError::StaleReplica(error) => Self::StaleReplica(error),
        }
    }
}

/// A typed session command deferred until the next save (V1 `Defer`).
#[derive(Clone, Debug, PartialEq)]
pub enum SessionCommand {
    /// Stage one validated family write into the save transaction, after
    /// the document puts, in V1's batch order.
    Family(FamilyWriteRequest),
}

impl SessionCommand {
    /// Wraps a family write request.
    #[must_use]
    pub fn family(value: FamilyWriteRequest) -> Self {
        Self::Family(value)
    }
}

/// The closed change-type vocabulary (V1 `DocumentsChanges.ChangeType`).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FieldChangeKind {
    /// The whole document is deleted by this session.
    DocumentDeleted,
    /// The whole document is added by this session.
    DocumentAdded,
    /// One field's value changed.
    FieldChanged,
    /// One field was added.
    NewField,
    /// One field was removed.
    RemovedField,
    /// One array slot changed value in place.
    ArrayValueChanged,
    /// One array slot was appended.
    ArrayValueAdded,
    /// One array slot was removed.
    ArrayValueRemoved,
}

/// One field-level change in a `WhatChanged` report (V1
/// `DocumentsChanges`).
#[derive(Clone, Debug, PartialEq)]
pub struct FieldChange {
    path: String,
    name: String,
    old: Option<DocumentValue>,
    new: Option<DocumentValue>,
    change: FieldChangeKind,
}

impl FieldChange {
    /// Creates one field change from its report facts.
    #[must_use]
    pub fn new(
        path: String,
        name: String,
        old: Option<DocumentValue>,
        new: Option<DocumentValue>,
        change: FieldChangeKind,
    ) -> Self {
        Self { path, name, old, new, change }
    }

    /// Returns the dotted path of the containing field, empty at the root.
    #[must_use]
    pub fn field_path(&self) -> &str {
        &self.path
    }

    /// Returns the changed field's name (an index as text for array items).
    #[must_use]
    pub fn field_name(&self) -> &str {
        &self.name
    }

    /// Returns path plus name, or the name alone at the root — V1's
    /// `FieldFullName`.
    #[must_use]
    pub fn field_full_name(&self) -> String {
        if self.path.is_empty() {
            self.name.clone()
        } else {
            format!("{}.{}", self.path, self.name)
        }
    }

    /// Returns the old value, absent for added fields and document-level
    /// reports.
    #[must_use]
    pub fn old_value(&self) -> Option<&DocumentValue> {
        self.old.as_ref()
    }

    /// Returns the new value, absent for removed fields and document-level
    /// reports.
    #[must_use]
    pub fn new_value(&self) -> Option<&DocumentValue> {
        self.new.as_ref()
    }

    /// Returns the change type.
    #[must_use]
    pub const fn change(&self) -> FieldChangeKind {
        self.change
    }
}

/// One document's `WhatChanged` report.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentChanges {
    collection: CollectionName,
    id: DocumentId,
    changes: Vec<FieldChange>,
}

impl DocumentChanges {
    /// Creates one report from its parts.
    #[must_use]
    pub fn new(collection: CollectionName, id: DocumentId, changes: Vec<FieldChange>) -> Self {
        Self { collection, id, changes }
    }

    /// Returns the owning collection.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the document identity.
    #[must_use]
    pub const fn id(&self) -> &DocumentId {
        &self.id
    }

    /// Returns the field-level changes.
    #[must_use]
    pub fn changes(&self) -> &[FieldChange] {
        &self.changes
    }
}

/// The argument of a `before_store` hook: the put being prepared, with a
/// mutable value.
pub struct BeforeStoreEvent {
    collection: CollectionName,
    id: DocumentId,
    value: DocumentValue,
}

impl BeforeStoreEvent {
    /// Returns the collection of the prepared put.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the identity of the prepared put.
    #[must_use]
    pub const fn id(&self) -> &DocumentId {
        &self.id
    }

    /// Returns the staged value.
    #[must_use]
    pub const fn value(&self) -> &DocumentValue {
        &self.value
    }

    /// Returns the staged value mutably: a `before_store` hook may edit the
    /// document before it is staged, like V1's `OnBeforeStore`.
    #[must_use]
    pub fn value_mut(&mut self) -> &mut DocumentValue {
        &mut self.value
    }
}

/// The argument of a `before_delete` hook: the delete being prepared.
pub struct BeforeDeleteEvent {
    collection: CollectionName,
    id: DocumentId,
}

impl BeforeDeleteEvent {
    /// Returns the collection of the prepared delete.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the identity of the prepared delete.
    #[must_use]
    pub const fn id(&self) -> &DocumentId {
        &self.id
    }
}

/// The argument of an `after_save` hook: the commit that succeeded.
pub struct AfterSaveEvent {
    operation_id: OperationId,
    sequence: StorageSequence,
}

impl AfterSaveEvent {
    /// Returns the stable operation identity of the successful save.
    #[must_use]
    pub const fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the storage sequence the save committed at.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }
}

/// A hook's typed veto: the save aborts with nothing staged or committed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SessionHookVeto {
    reason: String,
}

impl SessionHookVeto {
    /// Creates a veto carrying its reason.
    #[must_use]
    pub fn new(reason: impl Into<String>) -> Self {
        Self { reason: reason.into() }
    }

    /// Returns the veto reason.
    #[must_use]
    pub fn reason(&self) -> &str {
        &self.reason
    }
}

/// A `before_store` hook: may mutate the staged value; `Err` vetoes the save.
pub type BeforeStoreHook =
    Box<dyn FnMut(&mut BeforeStoreEvent) -> Result<(), SessionHookVeto> + Send>;

/// A `before_delete` hook: `Err` vetoes the save.
pub type BeforeDeleteHook =
    Box<dyn FnMut(&BeforeDeleteEvent) -> Result<(), SessionHookVeto> + Send>;

/// An `after_save` observation hook: never vetoes.
pub type AfterSaveHook = Box<dyn FnMut(&AfterSaveEvent) + Send>;

#[derive(Default)]
struct SessionHooks {
    before_store: Vec<BeforeStoreHook>,
    before_delete: Vec<BeforeDeleteHook>,
    after_save: Vec<AfterSaveHook>,
}

/// One tracked document: the pinned value it was loaded with, the value
/// staged now, and the expected version the next save stages it under (the
/// last-write-wins override a `store_checked` records).
struct TrackedDocument {
    original: Document,
    current: Document,
    ignored: bool,
    expected: ExpectedVersion,
}

/// One deferred put awaiting the next save.
struct PendingPut {
    value: DocumentValue,
    expected: ExpectedVersion,
}

/// The identity-map key: storage keys a document by tenant, collection,
/// and identifier.
type DocKey = (CollectionName, DocumentId);

/// The engine-side session (the GAP-D01 unit of work).
///
/// Stores, deletes, and deferred commands queue until
/// [`Session::save_changes`] stages them into one engine transaction in
/// V1's batch order — deletes, then puts, then deferred commands — and
/// publishes once. Pre-commit refusals (hook vetoes, deferred-vs-delete
/// validation, no-tracking) commit nothing; a rejected or unknown commit
/// outcome keeps the session state for retry.
pub struct Session<'engine> {
    engine: &'engine Engine,
    scope: TenantScope,
    options: SessionOptions,
    session_token: SessionToken,
    tracked: BTreeMap<DocKey, TrackedDocument>,
    known_missing: BTreeSet<DocKey>,
    pending_puts: BTreeMap<DocKey, PendingPut>,
    pending_deletes: BTreeMap<DocKey, ExpectedVersion>,
    deferred: Vec<FamilyWriteRequest>,
    pending_lazy: BTreeSet<DocKey>,
    pending_generated: Vec<(CollectionName, DocumentValue)>,
    generated: Vec<DocKey>,
    lazy_batches: u64,
    hooks: SessionHooks,
    operation_counter: u64,
    identity: u64,
}

impl fmt::Debug for Session<'_> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("Session")
            .field("options", &self.options)
            .field("tracked", &self.tracked.len())
            .field("known_missing", &self.known_missing.len())
            .field("pending_puts", &self.pending_puts.len())
            .field("pending_deletes", &self.pending_deletes.len())
            .field("deferred", &self.deferred.len())
            .field("pending_lazy", &self.pending_lazy.len())
            .field("lazy_batches", &self.lazy_batches)
            .finish_non_exhaustive()
    }
}

/// Mints session identities for operation-id derivation.
fn next_session_identity() -> u64 {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    COUNTER.fetch_add(1, Ordering::Relaxed)
}

impl<'engine> Session<'engine> {
    /// Opens one session after validating the scope against the Engine.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::ScopeMismatch`] when the scope names another
    /// Engine and [`SessionError::ClusterWideUnavailable`] for the
    /// `ClusterWide` mode, which fails closed until cluster transactions
    /// exist.
    pub fn open(
        engine: &'engine Engine,
        scope: &TenantScope,
        options: SessionOptions,
    ) -> Result<Self, SessionError> {
        // read_snapshot is the engine's own scope validator; it touches no
        // session state.
        engine.read_snapshot(scope)?;
        if options.transaction_mode == SessionTransactionMode::ClusterWide {
            return Err(SessionError::ClusterWideUnavailable);
        }
        let epoch = engine.session_epoch();
        let session_token =
            options.session_token.clone().unwrap_or_else(|| SessionToken::new(epoch));
        if session_token.epoch() != epoch {
            return Err(SessionError::SessionToken(SessionTokenError::EpochMismatch {
                expected: epoch,
                observed: session_token.epoch(),
            }));
        }
        Ok(Self {
            engine,
            scope: scope.clone(),
            options,
            session_token,
            tracked: BTreeMap::new(),
            known_missing: BTreeSet::new(),
            pending_puts: BTreeMap::new(),
            pending_deletes: BTreeMap::new(),
            deferred: Vec::new(),
            pending_lazy: BTreeSet::new(),
            pending_generated: Vec::new(),
            generated: Vec::new(),
            lazy_batches: 0,
            hooks: SessionHooks::default(),
            operation_counter: 0,
            identity: next_session_identity(),
        })
    }

    /// Returns the session's options.
    #[must_use]
    pub const fn options(&self) -> &SessionOptions {
        &self.options
    }

    /// Returns the current opaque token, including observations made by this
    /// session's successful reads and saves.
    #[must_use]
    pub const fn session_token(&self) -> &SessionToken {
        &self.session_token
    }

    /// Returns the encoded token suitable for a subsequent request or
    /// session, without exposing its causal fields to the caller.
    #[must_use]
    pub fn encoded_session_token(&self) -> Vec<u8> {
        self.session_token.encode()
    }

    /// Reports the number of tracked documents.
    #[must_use]
    pub fn tracked_count(&self) -> usize {
        self.tracked.len()
    }

    // -- R2: deferred puts --------------------------------------------------

    /// Stages one overwrite put (`ExpectedVersion::Any`), deferred until the
    /// next save — V1 `Store(entity, id)` overwrite semantics.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn store(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
        value: DocumentValue,
    ) -> Result<(), SessionError> {
        self.put(collection, id, value, ExpectedVersion::Any)
    }

    /// Stages one create put (`ExpectedVersion::New`): the save conflicts if
    /// the document already exists — V1's forced-concurrency store for ids
    /// it could not extract.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn store_new(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
        value: DocumentValue,
    ) -> Result<(), SessionError> {
        self.put(collection, id, value, ExpectedVersion::New)
    }

    /// Stages one put with an explicit expected version and forces the
    /// concurrency check. The staged expectation is last-write-wins session state:
    /// a later `store` or `store_checked` on the same key replaces it, and
    /// a successful save consumes it (the re-based document saves unchecked
    /// again until a new checked store).
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn store_checked(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
        value: DocumentValue,
        expected: ExpectedVersion,
    ) -> Result<(), SessionError> {
        self.put(collection, id, value, expected)
    }

    /// Reserves one no-explicit-ID store (GAP-D05 engine half): the id is
    /// allocated from the collection's identity family at save, inside the
    /// same transaction, and reported by [`Session::generated_ids`].
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn store_generated(
        &mut self,
        collection: CollectionName,
        value: DocumentValue,
    ) -> Result<(), SessionError> {
        self.require_tracking()?;
        self.pending_generated.push((collection, value));
        Ok(())
    }

    fn put(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
        value: DocumentValue,
        expected: ExpectedVersion,
    ) -> Result<(), SessionError> {
        self.require_tracking()?;
        let key = (collection, id);
        if let Some(pending) = self.pending_puts.get_mut(&key) {
            pending.value = value;
            pending.expected = expected;
            return Ok(());
        }
        // A store subsumes a queued delete for the same key. V1's single
        // batch applies a delete+put pair to the stored value, and the
        // engine's one-write-per-target transaction contract means one
        // staged put produces exactly that committed outcome — so the put
        // replaces the delete instead of staging beside it. Store also
        // clears known-missing, like V1's `StoreInternal`.
        self.pending_deletes.remove(&key);
        self.known_missing.remove(&key);
        if let Some(tracked) = self.tracked.get_mut(&key) {
            tracked.current = Document::new(key.1.clone(), key.0.clone(), value);
            tracked.expected = expected;
            return Ok(());
        }
        self.pending_puts.insert(key, PendingPut { value, expected });
        Ok(())
    }

    // -- R3: deferred deletes -----------------------------------------------

    /// Stages one delete (`ExpectedVersion::Any`), deferred until the next
    /// save — V1 `Delete(string id)`. The delete cancels a pending put for
    /// the same key (V1's Delete replaces an earlier Store) and registers
    /// known-missing, so loads answer absent until the save commits or a
    /// later store subsumes the delete.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn delete(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
    ) -> Result<(), SessionError> {
        self.remove(collection, id, ExpectedVersion::Any)
    }

    /// Stages one delete with an explicit expected version — V1
    /// `Delete(string id, string expectedChangeVector)`.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn delete_expected(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
        expected: ExpectedVersion,
    ) -> Result<(), SessionError> {
        self.remove(collection, id, expected)
    }

    fn remove(
        &mut self,
        collection: CollectionName,
        id: DocumentId,
        expected: ExpectedVersion,
    ) -> Result<(), SessionError> {
        self.require_tracking()?;
        let key = (collection, id);
        // Delete replaces an earlier store. The identity leaves the map and
        // registers known-missing, so a pending put cannot reach the batch or
        // resurrect on a later save.
        self.pending_puts.remove(&key);
        self.known_missing.insert(key.clone());
        self.pending_deletes.insert(key, expected);
        Ok(())
    }

    // -- R4: loads and the identity map --------------------------------------

    /// Loads one document: pending puts answer first (read-your-writes),
    /// then a pending delete answers absent (the delete registered
    /// known-missing, like V1's `Load` on a session-deleted id), then the
    /// pinned identity-map instance, then one engine read. An absent
    /// document registers known-missing.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::Engine`] when the engine read fails.
    pub fn load(
        &mut self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Option<Document>, SessionError> {
        let answers = self.load_many(collection, std::slice::from_ref(id))?;
        Ok(answers.into_iter().next().flatten())
    }

    /// Loads a batch of documents in request order, resolving every
    /// identity-map miss in one snapshot pass.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::Engine`] when the engine read fails.
    pub fn load_many(
        &mut self,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Result<Vec<Option<Document>>, SessionError> {
        let mut answers: Vec<Option<Document>> = vec![None; ids.len()];
        let mut misses: Vec<usize> = Vec::new();
        for (index, id) in ids.iter().enumerate() {
            let key = (collection.clone(), id.clone());
            if let Some(pending) = self.pending_puts.get(&key) {
                answers[index] =
                    Some(Document::new(id.clone(), collection.clone(), pending.value.clone()));
            } else if self.pending_deletes.contains_key(&key) {
                // A pending delete answers absent without a read: the delete
                // registered known-missing, and V1's `Load` checks the
                // deleted set before the identity map, so a session-deleted
                // id answers null even while a tracked instance remains.
            } else if let Some(tracked) = self.tracked.get(&key) {
                answers[index] = Some(tracked.current.clone());
            } else if !self.known_missing.contains(&key) {
                misses.push(index);
            } else {
                // Known-missing: the answer stays `None` without a read.
            }
        }
        if misses.is_empty() {
            return Ok(answers);
        }
        self.enforce_read_consistency()?;
        let operation = self.next_operation_id();
        let snapshot = self.engine.read_snapshot(&self.scope)?;
        let transaction = self.engine.begin(&self.scope, snapshot, operation)?;
        for index in misses {
            let id = ids[index].clone();
            let document =
                transaction.read_in_collection(collection, &id).map_err(SessionError::from)?;
            let key = (collection.clone(), id);
            match document {
                Some(document) => {
                    answers[index] = Some(document.clone());
                    if self.options.tracking == SessionTracking::Tracked {
                        self.tracked.insert(
                            key,
                            TrackedDocument {
                                original: document.clone(),
                                current: document,
                                ignored: false,
                                expected: ExpectedVersion::Any,
                            },
                        );
                    }
                }
                None => {
                    if self.options.tracking == SessionTracking::Tracked {
                        self.known_missing.insert(key);
                    }
                }
            }
        }
        self.observe_read_frontier()?;
        Ok(answers)
    }

    // -- R5: refresh, evict, clear -------------------------------------------

    /// Re-reads one tracked document from a fresh engine snapshot and
    /// replaces both the pinned original and the current value — V1
    /// `Refresh<T>` discards local changes.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NotTracked`] when the document is not
    /// tracked and [`SessionError::Engine`] when the engine read fails.
    pub fn refresh(
        &mut self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<(), SessionError> {
        let key = (collection.clone(), id.clone());
        if !self.tracked.contains_key(&key) {
            return Err(SessionError::NotTracked { document: id.clone() });
        }
        self.enforce_read_consistency()?;
        let operation = self.next_operation_id();
        let snapshot = self.engine.read_snapshot(&self.scope)?;
        let transaction = self.engine.begin(&self.scope, snapshot, operation)?;
        if let Some(document) =
            transaction.read_in_collection(collection, id).map_err(SessionError::from)?
        {
            self.tracked.insert(
                key.clone(),
                TrackedDocument {
                    original: document.clone(),
                    current: document,
                    ignored: false,
                    expected: ExpectedVersion::Any,
                },
            );
            self.known_missing.remove(&key);
        } else {
            self.tracked.remove(&key);
            self.known_missing.insert(key);
        }
        self.observe_read_frontier()?;
        Ok(())
    }

    /// Evicts one document: it leaves the identity map, the known-missing
    /// set, the pending-lazy set, the delete queue, and the pending puts —
    /// V1 `Evict<T>`.
    pub fn evict(&mut self, collection: &CollectionName, id: &DocumentId) {
        let key = (collection.clone(), id.clone());
        self.tracked.remove(&key);
        self.known_missing.remove(&key);
        self.pending_lazy.remove(&key);
        self.pending_deletes.remove(&key);
        self.pending_puts.remove(&key);
    }

    /// Clears the whole session: identity map, known-missing, pending puts,
    /// deletes, deferred commands, pending lazy reads, and generated-id
    /// reservations — V1 `Clear`.
    pub fn clear(&mut self) {
        self.tracked.clear();
        self.known_missing.clear();
        self.pending_puts.clear();
        self.pending_deletes.clear();
        self.deferred.clear();
        self.pending_lazy.clear();
        self.pending_generated.clear();
        self.generated.clear();
    }

    /// Reports whether the id is loaded: pinned, known-missing, or staged by
    /// a pending put — V1 `IsLoaded` answers known-missing ids too.
    #[must_use]
    pub fn is_loaded(&self, collection: &CollectionName, id: &DocumentId) -> bool {
        let key = &(collection.clone(), id.clone());
        self.tracked.contains_key(key)
            || self.known_missing.contains(key)
            || self.pending_puts.contains_key(key)
    }

    /// Reports whether the id is known-missing: loaded and found absent, or
    /// deleted in this session (V1's `IsDeleted` registration, which `Load`
    /// consults before the identity map).
    #[must_use]
    pub fn is_known_missing(&self, collection: &CollectionName, id: &DocumentId) -> bool {
        self.known_missing.contains(&(collection.clone(), id.clone()))
    }

    /// Marks one tracked document ignored for change tracking: it stays
    /// loaded but no save or report includes it — V1 `IgnoreChangesFor`.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NotTracked`] when the document is not
    /// tracked.
    pub fn ignore_changes_for(
        &mut self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<(), SessionError> {
        let key = (collection.clone(), id.clone());
        match self.tracked.get_mut(&key) {
            Some(tracked) => {
                tracked.ignored = true;
                Ok(())
            }
            None => Err(SessionError::NotTracked { document: id.clone() }),
        }
    }

    // -- R6: WhatChanged ------------------------------------------------------

    /// Reports every changed document in canonical (collection, id) order,
    /// comparing staged values against the pinned originals. Idempotent:
    /// a second call reports the same thing (V1 `WhatChanged`).
    ///
    /// Generated no-explicit-ID reservations report through the puts they
    /// become at save, not here: their ids do not exist until the identity
    /// family allocates them.
    #[must_use]
    pub fn what_changed(&self) -> Vec<DocumentChanges> {
        let mut keys: BTreeSet<DocKey> = BTreeSet::new();
        keys.extend(self.pending_puts.keys().cloned());
        keys.extend(self.pending_deletes.keys().cloned());
        keys.extend(
            self.tracked
                .iter()
                .filter(|(_, tracked)| {
                    !tracked.ignored && tracked.current.value() != tracked.original.value()
                })
                .map(|(key, _)| key.clone()),
        );
        let mut report = Vec::new();
        for key in keys {
            let (collection, id) = key.clone();
            let mut changes = Vec::new();
            if self.pending_deletes.contains_key(&key) {
                changes.push(FieldChange::new(
                    String::new(),
                    String::new(),
                    None,
                    None,
                    FieldChangeKind::DocumentDeleted,
                ));
            } else if self.pending_puts.contains_key(&key) {
                changes.push(FieldChange::new(
                    String::new(),
                    String::new(),
                    None,
                    None,
                    FieldChangeKind::DocumentAdded,
                ));
            } else if let Some(tracked) = self.tracked.get(&key) {
                diff_values("", tracked.original.value(), tracked.current.value(), &mut changes);
            }
            if !changes.is_empty() {
                report.push(DocumentChanges::new(collection, id, changes));
            }
        }
        report
    }

    /// Reports whether one document has queued changes.
    #[must_use]
    pub fn has_changed(&self, collection: &CollectionName, id: &DocumentId) -> bool {
        let key = &(collection.clone(), id.clone());
        self.pending_puts.contains_key(key)
            || self.pending_deletes.contains_key(key)
            || self.tracked.get(key).is_some_and(|tracked| {
                !tracked.ignored && tracked.current.value() != tracked.original.value()
            })
    }

    /// Reports whether any change, delete, deferred command, or generated
    /// store is queued — V1 `HasChanges`.
    #[must_use]
    pub fn has_changes(&self) -> bool {
        !self.what_changed().is_empty()
            || !self.deferred.is_empty()
            || !self.pending_generated.is_empty()
    }

    // -- R9: lazy reads --------------------------------------------------------

    /// Registers one pending lazy read without executing it — V1 lazy
    /// `Load`. The pending set executes as one batch on
    /// [`Session::execute_all_pending_lazy_operations`] or at the next save.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session:
    /// a lazy read's result only exists through tracking.
    pub fn lazy_load(
        &mut self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<(), SessionError> {
        self.require_tracking()?;
        self.pending_lazy.insert((collection.clone(), id.clone()));
        Ok(())
    }

    /// Executes the whole pending lazy set as one batched load — V1
    /// `ExecuteAllPendingLazyOperations`. Each execution increments
    /// [`Session::lazy_batches_executed`] once, however many reads it
    /// resolved. The engine's batch read is per-collection, so a pending
    /// set spanning several collections resolves as one batched load per
    /// collection within the single execution.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::Engine`] when the engine read fails.
    pub fn execute_all_pending_lazy_operations(&mut self) -> Result<(), SessionError> {
        self.require_tracking()?;
        if self.pending_lazy.is_empty() {
            return Ok(());
        }
        let pending: Vec<DocKey> = self.pending_lazy.iter().cloned().collect();
        let mut by_collection: BTreeMap<CollectionName, Vec<DocumentId>> = BTreeMap::new();
        for (collection, id) in pending {
            by_collection.entry(collection).or_default().push(id);
        }
        for (collection, ids) in by_collection {
            self.load_many(&collection, &ids)?;
        }
        self.pending_lazy.clear();
        self.lazy_batches = self.lazy_batches.saturating_add(1);
        Ok(())
    }

    /// Returns how many lazy batches have executed (one per execution of a
    /// non-empty pending set).
    #[must_use]
    pub const fn lazy_batches_executed(&self) -> u64 {
        self.lazy_batches
    }

    // -- R8: deferred commands -------------------------------------------------

    /// Defers one typed command to the next save — V1 `Defer`. The command
    /// stages into the save transaction after the document puts and commits
    /// atomically with them.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session.
    pub fn defer(&mut self, command: SessionCommand) -> Result<(), SessionError> {
        self.require_tracking()?;
        let SessionCommand::Family(request) = command;
        self.deferred.push(request);
        Ok(())
    }

    // -- R11: hooks -------------------------------------------------------------

    /// Registers a `before_store` hook (V1 `OnBeforeStore`): it observes
    /// every put the next save prepares, may mutate the staged value, and
    /// vetoes the save on `Err` with nothing staged or committed.
    pub fn on_before_store(&mut self, hook: BeforeStoreHook) {
        self.hooks.before_store.push(hook);
    }

    /// Registers a `before_delete` hook (V1 `OnBeforeDelete`): it observes
    /// every delete the next save prepares and vetoes the save on `Err`
    /// with nothing staged or committed.
    pub fn on_before_delete(&mut self, hook: BeforeDeleteHook) {
        self.hooks.before_delete.push(hook);
    }

    /// Registers an `after_save` hook (V1 `OnAfterSaveChanges`): it
    /// observes each successful save exactly once; it never vetoes.
    pub fn on_after_save(&mut self, hook: AfterSaveHook) {
        self.hooks.after_save.push(hook);
    }

    // -- R7: the unit-of-work boundary -------------------------------------------

    /// Saves every queued change: pending lazy reads execute as one batch,
    /// save-time validation and hooks run before anything is staged, and
    /// one engine transaction stages deletes, then puts, then deferred
    /// commands — V1's batch order — before a single commit.
    ///
    /// Returns `Ok(None)` when nothing is queued (V1's no-op `SaveChanges`
    /// sends nothing), `Ok(Some(..))` with the engine's commit outcome
    /// otherwise, and `Err` for every pre-commit refusal, which stages and
    /// commits nothing.
    ///
    /// # Panics
    ///
    /// Never: the derived decimal identifiers and session operation
    /// identities are constructed from validated parts.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::NoTrackingRefused`] on a no-tracking session,
    /// [`SessionError::DeferredCommandOnDeletedDocument`] when a deferred
    /// command guards a document this save also deletes,
    /// [`SessionError::HookVetoed`] when a lifecycle hook vetoes, and the
    /// mapped engine or family error otherwise.
    #[allow(
        clippy::too_many_lines,
        reason = "one unit-of-work boundary in V1's single-batch order; splitting it would hide the ordering contract"
    )]
    pub fn save_changes(&mut self) -> Result<Option<CommitOutcome>, SessionError> {
        self.require_tracking()?;
        if self.options.transaction_mode == SessionTransactionMode::ClusterWide {
            return Err(SessionError::ClusterWideUnavailable);
        }
        self.execute_all_pending_lazy_operations()?;

        let deletes: Vec<(DocKey, ExpectedVersion)> =
            self.pending_deletes.iter().map(|(key, expected)| (key.clone(), *expected)).collect();
        // A key never holds both a pending put and a pending delete: each
        // command cancels the other when it is queued last, which is how
        // the one-write-per-target engine transaction receives the pair
        // (V1's delete+put batch leaves the stored value in place).
        let mut puts: BTreeMap<DocKey, (DocumentValue, ExpectedVersion)> = self
            .pending_puts
            .iter()
            .map(|(key, pending)| (key.clone(), (pending.value.clone(), pending.expected)))
            .collect();
        for (key, tracked) in &self.tracked {
            if tracked.ignored
                || self.pending_deletes.contains_key(key)
                || tracked.current.value() == tracked.original.value()
            {
                continue;
            }
            puts.insert(key.clone(), (tracked.current.value().clone(), tracked.expected));
        }
        // The reservations are read, not taken: a refused or rejected save
        // keeps them queued, like every other piece of session state.
        let generated_pending = self.pending_generated.clone();
        let mut identity_allocations: Vec<(String, u64)> = Vec::new();
        if deletes.is_empty()
            && puts.is_empty()
            && self.deferred.is_empty()
            && generated_pending.is_empty()
        {
            return Ok(None);
        }

        // Save-time validation: a deferred command guarding a deleted
        // document refuses the whole save before anything is staged.
        for request in &self.deferred {
            if let Some(document) = guarded_document(request)
                && deletes.iter().any(|((_, id), _)| id == document)
            {
                return Err(SessionError::DeferredCommandOnDeletedDocument {
                    document: document.clone(),
                });
            }
        }

        // The save's operation identity is minted before the hook borrows:
        // every staged write and deferred command shares it.
        let operation = self.next_operation_id();
        let snapshot = self.engine.read_snapshot(&self.scope)?;
        // Capture the causal cut before publishing. A successful commit must
        // be followed by an observation that advances this cut; merely
        // reading an unrelated (or stale) frontier is not enough to maintain
        // the session guarantee.
        let write_baseline =
            self.engine.session_frontier(&self.scope).map_err(SessionError::from)?;

        // Generated ids are derived before any staging: the range is read
        // on this save's snapshot, the puts join the batch below, and the
        // allocation itself stages inside the save transaction — so a vetoed
        // or crashed save leaks no ids.
        let mut generated: Vec<DocKey> = Vec::new();
        if !generated_pending.is_empty() {
            let counts = generated_identity_counts(&generated_pending);
            let names: Vec<String> = counts.iter().map(|(name, _)| name.clone()).collect();
            let context = FamilyReadContext::try_new(self.scope.clone(), snapshot.clone())?;
            let request = FamilyReadRequest::identity_range(
                IdentityRead::try_new(names, fresh_deadline(), Cancellation::new())
                    .expect("validated identity read"),
            );
            let next_by_name = match self.engine.families().read(context, request)? {
                FamilyReadResult::IdentityRange(result) => result
                    .entries()
                    .iter()
                    .map(|entry| (entry.name().to_owned(), entry.next()))
                    .collect::<BTreeMap<String, u64>>(),
                _ => return Err(SessionError::Family(FamilyError::InvalidRequest)),
            };
            let mut cursors: BTreeMap<String, u64> = BTreeMap::new();
            for (collection, value) in generated_pending {
                let name = format!("{IDENTITY_NAME_PREFIX}{}", collection.as_str());
                let next = next_by_name.get(&name).copied().unwrap_or(0);
                let taken = cursors.entry(name).or_insert(next);
                let id = DocumentId::try_new(&taken.to_string())
                    .expect("decimal ids are valid identifiers");
                *taken = taken.saturating_add(1);
                puts.insert((collection.clone(), id.clone()), (value, ExpectedVersion::New));
                generated.push((collection, id));
            }
            identity_allocations = counts;
        }

        // Hooks: deletes first, then puts, matching V1's save preparation.
        // Hook edits persist to the session's staged state, like V1's
        // mutated entities, so a retried save keeps them.
        let SessionHooks { before_store, before_delete, after_save } = &mut self.hooks;
        for ((collection, id), _) in &deletes {
            let event = BeforeDeleteEvent { collection: collection.clone(), id: id.clone() };
            for hook in before_delete.iter_mut() {
                if let Err(veto) = hook(&event) {
                    return Err(SessionError::HookVetoed { reason: veto.reason().to_owned() });
                }
            }
        }
        for (key, staged) in &mut puts {
            let mut event = BeforeStoreEvent {
                collection: key.0.clone(),
                id: key.1.clone(),
                value: staged.0.clone(),
            };
            for hook in before_store.iter_mut() {
                if let Err(veto) = hook(&mut event) {
                    return Err(SessionError::HookVetoed { reason: veto.reason().to_owned() });
                }
            }
            staged.0 = event.value().clone();
            if let Some(pending) = self.pending_puts.get_mut(key) {
                pending.value = staged.0.clone();
            }
            if let Some(tracked) = self.tracked.get_mut(key) {
                tracked.current = Document::new(key.1.clone(), key.0.clone(), staged.0.clone());
            }
        }

        let mut transaction =
            self.engine.begin(&self.scope, snapshot.clone(), operation.clone())?;

        // The identity allocation rides the save transaction: it publishes
        // with the puts that use it or not at all.
        for (name, count) in identity_allocations {
            let write = IdentityWrite::try_new(
                self.scope.clone(),
                name.clone(),
                IdentityCommand::Allocate { name: name.clone(), count },
                operation.clone(),
                fresh_deadline(),
                Cancellation::new(),
            )
            .expect("validated identity write");
            self.engine
                .families()
                .stage(&mut transaction, FamilyWriteRequest::identity_allocation(write))?;
        }

        // V1 batch order: deletes, then puts, then deferred commands.
        for ((_, id), expected) in &deletes {
            transaction.delete(id.clone(), *expected).map_err(SessionError::from)?;
        }
        for ((collection, id), (value, expected)) in &puts {
            transaction
                .put(Document::new(id.clone(), collection.clone(), value.clone()), *expected)
                .map_err(SessionError::from)?;
        }
        for request in &self.deferred {
            // The family stage contract requires the request's identity to
            // equal the save transaction's; the session owns that identity,
            // so deferred commands rebase onto it at save.
            let mut rebased = request.clone();
            rebased.rebase_operation(operation.clone());
            self.engine.families().stage(&mut transaction, rebased).map_err(SessionError::from)?;
        }
        let outcome = transaction.commit(self.options.durability);

        if let CommitOutcome::Committed(receipt) = &outcome {
            let observed = self.engine.session_frontier(&self.scope).map_err(SessionError::from)?;
            if !frontier_advanced(&write_baseline, &observed) {
                return Err(SessionError::WriteFrontierUnavailable);
            }
            // Committed state clears (V1
            // ClearSessionStateAfterSuccessfulSaveChanges): stored
            // documents re-base (the checked expectation is consumed),
            // deleted documents leave the map.
            for (key, (value, _)) in &puts {
                let document = Document::new(key.1.clone(), key.0.clone(), value.clone());
                self.pending_puts.remove(key);
                self.known_missing.remove(key);
                self.tracked.insert(
                    key.clone(),
                    TrackedDocument {
                        original: document.clone(),
                        current: document,
                        ignored: false,
                        expected: ExpectedVersion::Any,
                    },
                );
            }
            for (key, _) in &deletes {
                self.tracked.remove(key);
                self.known_missing.remove(key);
                // Defensive: no delete-masked pending put survives its own
                // save to resurrect on the next one.
                self.pending_puts.remove(key);
            }
            self.pending_deletes.clear();
            self.deferred.clear();
            self.pending_generated.clear();
            self.generated = generated;
            let event = AfterSaveEvent {
                operation_id: receipt.operation_id().clone(),
                sequence: receipt.sequence(),
            };
            for hook in after_save.iter_mut() {
                hook(&event);
            }
            // The commit's provenance is now part of the durable frontier.
            // Merge the exact post-commit observation only after the durable
            // advancement check above succeeds.
            self.session_token.merge(&SessionToken::from_frontier(observed))?;
        }
        Ok(Some(outcome))
    }

    /// Returns the ids the last successful save assigned to generated
    /// stores, in store order.
    #[must_use]
    pub fn generated_ids(&self) -> &[(CollectionName, DocumentId)] {
        &self.generated
    }

    // -- R10: streaming -----------------------------------------------------------

    /// Opens one document stream over a fresh snapshot — the session
    /// surface of W4-STREAM. Batches serve canonical key order with
    /// exactly-once terminal completion.
    ///
    /// # Errors
    ///
    /// Returns the mapped [`SessionError::Stream`] for an invalid source or
    /// scope.
    pub fn stream_documents(
        &self,
        selector: DocumentStreamSelector,
    ) -> Result<SessionStream<'engine>, SessionError> {
        self.enforce_read_consistency()?;
        let source = StreamSource::Documents(selector);
        let snapshot = self.engine.read_snapshot(&self.scope)?;
        let cursor = self.engine.streams().open(&self.scope, &snapshot, source.clone())?;
        Ok(SessionStream {
            engine: self.engine,
            scope: self.scope.clone(),
            policy: SessionReadPolicy::new(self.options.consistency, self.options.wait_timeout),
            session_token: self.session_token.clone(),
            snapshot,
            source,
            cursor,
            cancellation: Cancellation::new(),
        })
    }

    /// Loads one prefix page in canonical key order — V1
    /// `LoadStartingWith(idPrefix, matches, start, pageSize)`: skip `start`
    /// documents, then take `page_size`.
    ///
    /// # Panics
    ///
    /// Never: page sizes are converted within the `u32` range.
    ///
    /// # Errors
    ///
    /// Returns [`SessionError::Stream`] for an invalid selector and the
    /// mapped stream errors of the underlying batches.
    pub fn load_starting_with(
        &self,
        collection: CollectionName,
        prefix: &str,
        matches: Option<&str>,
        start: u32,
        page_size: u32,
    ) -> Result<Vec<Document>, SessionError> {
        let mut selector =
            DocumentStreamSelector::try_new(collection, fresh_deadline(), Cancellation::new())
                .map_err(|_| SessionError::Stream(StreamError::InvalidSource))?
                .with_prefix(prefix)
                .map_err(|_| SessionError::Stream(StreamError::InvalidSource))?;
        if let Some(pattern) = matches {
            selector = selector
                .with_match(pattern)
                .map_err(|_| SessionError::Stream(StreamError::InvalidSource))?;
        }
        let mut stream = self.stream_documents(selector)?;
        let mut documents = Vec::new();
        if page_size == 0 {
            return Ok(documents);
        }
        let wanted = usize::try_from(page_size).expect("page sizes fit usize");
        let mut skipped: u32 = 0;
        let limit = PAGE_BATCH_LIMIT.max(page_size.min(start.saturating_add(page_size)));
        while documents.len() < wanted {
            let batch = stream.next_batch(limit)?;
            let terminal = batch.completion().is_some();
            for item in batch.items() {
                if skipped < start {
                    skipped = skipped.saturating_add(1);
                    continue;
                }
                if let StreamItem::Document(document) = item {
                    documents.push(document.clone());
                    if documents.len() >= wanted {
                        break;
                    }
                }
            }
            if terminal {
                break;
            }
        }
        Ok(documents)
    }

    // -- helpers ------------------------------------------------------------------

    fn require_tracking(&self) -> Result<(), SessionError> {
        if self.options.tracking == SessionTracking::NoTracking {
            return Err(SessionError::NoTrackingRefused);
        }
        Ok(())
    }

    /// Waits for this session's current token before a non-eventual read.
    fn enforce_read_consistency(&self) -> Result<(), SessionError> {
        let policy = SessionReadPolicy::new(self.options.consistency, self.options.wait_timeout);
        let token = self.session_token.clone();
        let safe_region = token
            .frontier()
            .entries()
            .next()
            .map(|(origin, _)| origin.clone())
            .unwrap_or_else(|| RegionId::try_new("local").expect("local is a valid Region"));
        policy
            .enforce(&token, safe_region, || {
                self.engine
                    .session_frontier(&self.scope)
                    .map_err(|_| SessionTokenError::InvalidOrigin)
            })
            .map_err(SessionError::from)
    }

    /// Joins the latest durable frontier into this session token after a
    /// successful read. Joining, rather than replacing, is what preserves
    /// monotonic reads across collections and repeated batch calls.
    fn observe_read_frontier(&mut self) -> Result<(), SessionError> {
        let frontier = self
            .engine
            .session_frontier(&self.scope)
            .map_err(|_| SessionTokenError::InvalidOrigin)?;
        self.session_token.merge(&SessionToken::from_frontier(frontier))?;
        Ok(())
    }

    /// Mints the next never-reused operation identity of this session.
    fn next_operation_id(&mut self) -> OperationId {
        let id = OperationId::try_new(&format!(
            "terrabase-session-{}-{}",
            self.identity, self.operation_counter
        ))
        .expect("session operation identities are valid");
        self.operation_counter = self.operation_counter.saturating_add(1);
        id
    }
}

/// Returns whether `observed` contains progress beyond the pre-save cut.
///
/// A local commit always allocates a new per-origin sequence and HLC stamp,
/// while the frontier itself is a multi-origin vector. Checking both fields
/// keeps this guard correct when another origin advances concurrently and
/// makes a missing/empty provenance observation fail closed.
fn frontier_advanced(before: &SessionFrontier, observed: &SessionFrontier) -> bool {
    before.epoch() == observed.epoch()
        && (observed.last_write() > before.last_write()
            || observed.entries().any(|(origin, sequence)| sequence > before.sequence(origin)))
}

/// One open session document stream over the W4-STREAM seam.
pub struct SessionStream<'engine> {
    engine: &'engine Engine,
    scope: TenantScope,
    policy: SessionReadPolicy,
    session_token: SessionToken,
    snapshot: ReadSnapshot,
    source: StreamSource,
    cursor: StreamCursor,
    cancellation: Cancellation,
}

impl SessionStream<'_> {
    /// Returns the stream's current opaque token, including observations made
    /// while consuming its batches. The owning session can use this token to
    /// continue causality after the stream is dropped.
    #[must_use]
    pub const fn session_token(&self) -> &SessionToken {
        &self.session_token
    }

    /// Returns the encoded stream token for a subsequent session or request.
    #[must_use]
    pub fn encoded_session_token(&self) -> Vec<u8> {
        self.session_token.encode()
    }

    /// Serves one bounded batch: at most `limit` documents, the cursor that
    /// resumes exactly after them, and — exactly once at the end — the
    /// terminal completion.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError`] for a cancelled signal, an invalid limit or
    /// cursor, or the typed read error of the source.
    pub fn next_batch(&mut self, limit: u32) -> Result<StreamBatch, StreamError> {
        let safe_region = self
            .session_token
            .frontier()
            .entries()
            .next()
            .map(|(origin, _)| origin.clone())
            .unwrap_or_else(|| RegionId::try_new("local").expect("local is a valid Region"));
        self.policy
            .enforce(&self.session_token, safe_region, || {
                self.engine
                    .session_frontier(&self.scope)
                    .map_err(|_| SessionTokenError::InvalidOrigin)
            })
            .map_err(|error| match error {
                SessionReadError::Token(_) => StreamError::InvalidSource,
                SessionReadError::StaleReplica(stale) => {
                    StreamError::StaleReplica(stale.region().clone())
                }
            })?;
        let batch = self.engine.streams().next_batch(
            &self.scope,
            &self.snapshot,
            &self.source,
            self.cursor.clone(),
            limit,
            &self.cancellation,
        )?;
        self.cursor = batch.next_cursor().clone();
        let frontier =
            self.engine.session_frontier(&self.scope).map_err(|_| StreamError::InvalidSource)?;
        self.session_token
            .merge(&SessionToken::from_frontier(frontier))
            .map_err(|_| StreamError::InvalidSource)?;
        Ok(batch)
    }
}

/// Returns the document a family write guards, when it guards one.
fn guarded_document(request: &FamilyWriteRequest) -> Option<&DocumentId> {
    match request {
        FamilyWriteRequest::AttachmentMutation(write) => Some(write.document()),
        FamilyWriteRequest::CounterBatch(write) => Some(write.document()),
        FamilyWriteRequest::TimeSeriesBatch(write) => Some(write.document()),
        FamilyWriteRequest::RevisionCommand(write) => Some(write.document()),
        FamilyWriteRequest::LifecycleUpdate(write) => Some(write.document()),
        FamilyWriteRequest::ArchivalUpdate(write) => Some(write.document()),
        FamilyWriteRequest::TombstoneOperation(write) => Some(write.document()),
        FamilyWriteRequest::CompareExchangeCondition(_)
        | FamilyWriteRequest::IdentityAllocation(_)
        | FamilyWriteRequest::SchemaValidationUpdate(_) => None,
    }
}

/// Counts the identity allocation each collection's generated stores need,
/// in first-seen collection order.
fn generated_identity_counts(pending: &[(CollectionName, DocumentValue)]) -> Vec<(String, u64)> {
    let mut counts: Vec<(String, u64)> = Vec::new();
    for (collection, _) in pending {
        let name = format!("{IDENTITY_NAME_PREFIX}{}", collection.as_str());
        match counts.iter_mut().find(|(existing, _)| *existing == name) {
            Some((_, count)) => *count = count.saturating_add(1),
            None => counts.push((name, 1)),
        }
    }
    counts
}

/// Builds the fresh deadline every session-built family request carries.
fn fresh_deadline() -> Deadline {
    Deadline::after(FAMILY_DEADLINE).expect("a fresh 30s deadline never elapses at construction")
}

/// Diffs two document values into field-level changes with dotted paths.
fn diff_values(path: &str, old: &DocumentValue, new: &DocumentValue, out: &mut Vec<FieldChange>) {
    match (old, new) {
        (DocumentValue::Object(old_map), DocumentValue::Object(new_map)) => {
            let mut names: BTreeSet<&String> = old_map.keys().collect();
            names.extend(new_map.keys());
            for name in names {
                let child_path =
                    if path.is_empty() { name.clone() } else { format!("{path}.{name}") };
                match (old_map.get(name), new_map.get(name)) {
                    (Some(old_value), Some(new_value)) => {
                        if old_value == new_value {
                            continue;
                        }
                        match (old_value, new_value) {
                            (DocumentValue::Object(_), DocumentValue::Object(_)) => {
                                diff_values(&child_path, old_value, new_value, out);
                            }
                            (DocumentValue::Array(old_items), DocumentValue::Array(new_items)) => {
                                diff_array(&child_path, old_items, new_items, out);
                            }
                            _ => out.push(FieldChange::new(
                                path.to_owned(),
                                name.clone(),
                                Some(old_value.clone()),
                                Some(new_value.clone()),
                                FieldChangeKind::FieldChanged,
                            )),
                        }
                    }
                    (Some(old_value), None) => out.push(FieldChange::new(
                        path.to_owned(),
                        name.clone(),
                        Some(old_value.clone()),
                        None,
                        FieldChangeKind::RemovedField,
                    )),
                    (None, Some(new_value)) => out.push(FieldChange::new(
                        path.to_owned(),
                        name.clone(),
                        None,
                        Some(new_value.clone()),
                        FieldChangeKind::NewField,
                    )),
                    (None, None) => {}
                }
            }
        }
        (DocumentValue::Array(old_items), DocumentValue::Array(new_items)) => {
            diff_array(if path.is_empty() { "value" } else { path }, old_items, new_items, out);
        }
        _ => {
            if old != new {
                // A kind change reached without a field name of its own (the
                // root value, or a nested path handed down by a caller):
                // report the containing path and the leaf name, never the
                // full dotted name in `name`.
                let (containing, name) = split_leaf(path);
                out.push(FieldChange::new(
                    containing,
                    name,
                    Some(old.clone()),
                    Some(new.clone()),
                    FieldChangeKind::FieldChanged,
                ));
            }
        }
    }
}

/// Splits one dotted diff path into its containing path and leaf name. The
/// root names its value `value`; a single segment is a root field.
fn split_leaf(path: &str) -> (String, String) {
    match path.rsplit_once('.') {
        Some((containing, name)) => (containing.to_owned(), name.to_owned()),
        None => {
            if path.is_empty() {
                (String::new(), "value".to_owned())
            } else {
                (String::new(), path.to_owned())
            }
        }
    }
}

/// Diffs two arrays index-positionally: same index with a different value
/// is `ArrayValueChanged`, indices past the old length are added, indices
/// past the new length are removed.
fn diff_array(
    path: &str,
    old_items: &[DocumentValue],
    new_items: &[DocumentValue],
    out: &mut Vec<FieldChange>,
) {
    let shared = old_items.len().min(new_items.len());
    for (index, (old_item, new_item)) in
        old_items.iter().zip(new_items.iter()).enumerate().take(shared)
    {
        if old_item != new_item {
            out.push(FieldChange::new(
                path.to_owned(),
                index.to_string(),
                Some(old_item.clone()),
                Some(new_item.clone()),
                FieldChangeKind::ArrayValueChanged,
            ));
        }
    }
    for (index, new_item) in new_items.iter().enumerate().skip(shared) {
        out.push(FieldChange::new(
            path.to_owned(),
            index.to_string(),
            None,
            Some(new_item.clone()),
            FieldChangeKind::ArrayValueAdded,
        ));
    }
    for (index, old_item) in old_items.iter().enumerate().skip(shared) {
        out.push(FieldChange::new(
            path.to_owned(),
            index.to_string(),
            Some(old_item.clone()),
            None,
            FieldChangeKind::ArrayValueRemoved,
        ));
    }
}
