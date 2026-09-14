//! Reactive live queries: `subscribe` / `execute`, checkpoints, typed resets.
//!
//! `.subscribe()` uses bounded HTTP long-polling on `sync.api.` by default;
//! snapshot and log bodies are strict indexed `terrascale.sync.v1.msgpack`
//! envelopes with the manifest's allocation bounds.
//! [`Transport::WebSocket`] opts into direct member delivery. `.execute()`
//! is "subscribe once". A checkpoint is an opaque authorized-view position;
//! any mismatch returns a typed [`ResetReason`] with a fresh authorized
//! snapshot (GOAL D-46).

use std::cmp::Ordering;
use std::collections::{BTreeMap, VecDeque};
use std::fmt;
use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use base64::engine::{general_purpose::STANDARD as BASE64, Engine as _};
use serde::{Deserialize, Serialize};

use crate::client::{SharedClient, SKIP_AUTH_MARKER};
use crate::documents::DatabaseScope;
use crate::error::{Error, ErrorKind, Result};
use crate::regions::RegionCode;
use crate::sql::{Query, SqlRows};
use crate::transport::{Method, Request};

/// Delivery transport for a subscription.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Transport {
    /// Bounded HTTP long-polling on `sync.api.` (default: cacheable, collapsible).
    #[default]
    #[serde(rename = "http")]
    LongPolling,
    /// Direct opt-in transport; cannot be cached or collapsed.
    #[serde(rename = "websocket")]
    WebSocket,
}

/// Opaque authorized-view position. Never constructed by hand: it binds view
/// identity and generation, database/branch incarnation, schema version,
/// authorization revision, entitlement epoch, region/replica epoch, the
/// retained journal boundary, and expiry.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct Checkpoint {
    bytes: Vec<u8>,
}

const MAX_CHECKPOINT_BYTES: usize = 4096;
const MAX_POLL_WINDOW: Duration = Duration::from_secs(25);

#[derive(Deserialize)]
#[serde(untagged)]
enum CheckpointWire {
    Bytes(Vec<u8>),
    Text(String),
}

impl<'de> Deserialize<'de> for Checkpoint {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let wire = CheckpointWire::deserialize(deserializer)?;
        let bytes = match wire {
            CheckpointWire::Bytes(bytes) => bytes,
            CheckpointWire::Text(text) => BASE64.decode(&text).map_err(|error| {
                serde::de::Error::custom(format!("invalid checkpoint base64: {error}"))
            })?,
        };
        if !(1..=MAX_CHECKPOINT_BYTES).contains(&bytes.len()) {
            return Err(serde::de::Error::custom(
                "checkpoint must contain between 1 and 4096 bytes",
            ));
        }
        Ok(Self::new(bytes))
    }
}

impl Checkpoint {
    /// Wrap server-issued checkpoint bytes.
    #[must_use]
    pub fn new(bytes: Vec<u8>) -> Self {
        Self { bytes }
    }

    /// Construct a checkpoint while enforcing the published wire bound.
    pub fn try_new(bytes: Vec<u8>) -> Result<Self> {
        if !(1..=MAX_CHECKPOINT_BYTES).contains(&bytes.len()) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_checkpoint",
                "Checkpoint must contain between 1 and 4096 bytes.",
            ));
        }
        Ok(Self::new(bytes))
    }

    /// Raw bytes for the attach request.
    #[must_use]
    pub fn to_bytes(&self) -> Vec<u8> {
        self.bytes.clone()
    }

    /// Whether the server-issued token has no bytes and cannot resume.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.bytes.is_empty()
    }

    /// Base64 form for storage (local stores, URLs, logs).
    #[must_use]
    pub fn to_base64(&self) -> String {
        BASE64.encode(&self.bytes)
    }

    /// Restore from [`Checkpoint::to_base64`].
    pub fn from_base64(encoded: &str) -> Result<Self> {
        let bytes = BASE64.decode(encoded.trim()).map_err(|e| {
            Error::new(
                ErrorKind::Validation,
                "invalid_checkpoint",
                format!("Checkpoint is not valid base64: {e}. Resume with a fresh snapshot."),
            )
        })?;
        if !(1..=MAX_CHECKPOINT_BYTES).contains(&bytes.len()) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_checkpoint",
                "Checkpoint must contain between 1 and 4096 bytes. Resume with a fresh snapshot.",
            ));
        }
        Ok(Self::new(bytes))
    }
}

/// Why the server forced a reset instead of a delta. Every reason carries a
/// fresh authorized snapshot; the client replaces its view, it never replays.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ResetReason {
    PermissionChanged,
    AuthExpired,
    RegionMoved,
    SchemaChanged,
    GenerationChanged,
    HistoryExpired,
    SlowConsumer,
    Reconnect,
    CheckpointExpired,
    RetentionAdvanced,
    CheckpointNotCurrent,
    ViewDiverged,
    Oversized,
    InvalidOperations,
    InvalidCheckpoint,
    Backpressure,
    /// Catch-all for future server reasons; `detail` explains.
    ServerReset {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        detail: Option<String>,
    },
}

impl fmt::Display for ResetReason {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::PermissionChanged => write!(f, "permission_changed"),
            Self::AuthExpired => write!(f, "auth_expired"),
            Self::RegionMoved => write!(f, "region_moved"),
            Self::SchemaChanged => write!(f, "schema_changed"),
            Self::GenerationChanged => write!(f, "generation_changed"),
            Self::HistoryExpired => write!(f, "history_expired"),
            Self::SlowConsumer => write!(f, "slow_consumer"),
            Self::Reconnect => write!(f, "reconnect"),
            Self::CheckpointExpired => write!(f, "checkpoint_expired"),
            Self::RetentionAdvanced => write!(f, "retention_advanced"),
            Self::CheckpointNotCurrent => write!(f, "checkpoint_not_current"),
            Self::ViewDiverged => write!(f, "view_diverged"),
            Self::Oversized => write!(f, "oversized"),
            Self::InvalidOperations => write!(f, "invalid_operations"),
            Self::InvalidCheckpoint => write!(f, "invalid_checkpoint"),
            Self::Backpressure => write!(f, "backpressure"),
            Self::ServerReset { detail } => {
                write!(f, "server_reset")?;
                if let Some(detail) = detail {
                    write!(f, ": {detail}")?;
                }
                Ok(())
            }
        }
    }
}

/// Options for [`ReactiveClient::subscribe`].
#[derive(Debug, Clone, Default)]
pub struct SubscribeOptions {
    /// Delivery transport (default: long-polling).
    pub transport: Transport,
    /// Per-request region hint for view attach.
    pub region_hint: Option<RegionCode>,
    /// Resume position; absent means bootstrap from a fresh snapshot.
    pub checkpoint: Option<Checkpoint>,
    /// Client-local subscription identity carried in the MessagePack contract.
    pub subscription: Option<String>,
}

impl SubscribeOptions {
    /// Default options: long-polling, fresh snapshot.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Opt into the direct WebSocket transport.
    #[must_use]
    pub fn with_transport(mut self, transport: Transport) -> Self {
        self.transport = transport;
        self
    }

    /// Resume from a retained checkpoint.
    #[must_use]
    pub fn with_checkpoint(mut self, checkpoint: Checkpoint) -> Self {
        self.checkpoint = Some(checkpoint);
        self
    }

    /// Pin view attach to one region for this subscription.
    #[must_use]
    pub fn with_region_hint(mut self, region: RegionCode) -> Self {
        self.region_hint = Some(region);
        self
    }

    /// Set the client-local subscription identity.
    #[must_use]
    pub fn with_subscription(mut self, subscription: impl Into<String>) -> Self {
        self.subscription = Some(subscription.into());
        self
    }
}

/// A bounded retry budget for CDN log reads. A poll is one request; retries
/// only cover transport and server availability failures.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PollOptions {
    /// Maximum number of requests, including the first attempt.
    pub max_attempts: usize,
    /// Maximum duration of each bounded long-poll request (at most 25 s).
    pub deadline: Duration,
}

impl PollOptions {
    /// Construct a budget. Zero is normalized to one request.
    #[must_use]
    pub fn new(max_attempts: usize) -> Self {
        Self {
            max_attempts: max_attempts.max(1),
            deadline: MAX_POLL_WINDOW,
        }
    }

    /// Set a request deadline, capped at the protocol's 25-second window.
    #[must_use]
    pub fn with_deadline(mut self, deadline: Duration) -> Self {
        self.deadline = deadline.min(MAX_POLL_WINDOW);
        self
    }
}

impl Default for PollOptions {
    fn default() -> Self {
        Self::new(3)
    }
}

/// Cooperative cancellation for a long-poll or bounded retry sequence.
#[derive(Debug, Clone, Default)]
pub struct CancellationToken {
    cancelled: Arc<AtomicBool>,
    notification: Arc<tokio::sync::Notify>,
}

impl CancellationToken {
    /// Create a token that is initially active.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Request cancellation. In-flight HTTP futures are dropped by the
    /// polling operation, and no retry is started afterwards.
    pub fn cancel(&self) {
        self.cancelled.store(true, AtomicOrdering::Release);
        self.notification.notify_waiters();
    }

    /// Whether cancellation was requested.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(AtomicOrdering::Acquire)
    }

    pub(crate) async fn cancelled(&self) {
        loop {
            // Register before checking state so cancellation cannot be lost
            // between the check and suspension, including with cloned tokens.
            let notified = self.notification.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if self.is_cancelled() {
                return;
            }
            notified.await;
        }
    }
}

/// A typed reset emitted by the HTTP log. A snapshot is present when the
/// server can replace the local view immediately; otherwise the caller must
/// attach again and obtain a fresh authorized snapshot.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ResetFrame {
    pub reason: ResetReason,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub snapshot: Option<ViewSnapshot>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub checkpoint: Option<Checkpoint>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub region: Option<RegionCode>,
    /// Authorization generation carried by an authorization replacement.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authorization_generation: Option<u64>,
    /// Identities removed by an authorization transition.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub removed: Vec<String>,
    /// Complete-result digest carried by a replacement snapshot, when known.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub digest: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

/// One immutable CDN log response.
#[derive(Debug, Clone, PartialEq, Serialize, Default)]
pub struct PollResponse {
    /// Ordered deltas beginning at the requested offset.
    #[serde(default, alias = "updates")]
    pub deltas: Vec<ViewDelta>,
    /// Progress when the log has no data-bearing delta at this cut.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub checkpoint: Option<Checkpoint>,
    /// A typed replacement of the view.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reset: Option<ResetFrame>,
}

impl<'de> Deserialize<'de> for PollResponse {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        #[derive(Deserialize)]
        struct Wire {
            #[serde(default, alias = "updates")]
            deltas: Vec<ViewDelta>,
            #[serde(default)]
            checkpoint: Option<Checkpoint>,
            #[serde(default)]
            reset: Option<ResetFrame>,
        }

        // HTTP 204/304 and an empty long-poll timeout have no body. The
        // transport presents those as JSON null, which is an empty response.
        let value = Option::<Wire>::deserialize(deserializer)?;
        Ok(value.map_or_else(Self::default, |wire| Self {
            deltas: wire.deltas,
            checkpoint: wire.checkpoint,
            reset: wire.reset,
        }))
    }
}

/// Result of consuming one immutable log response.
#[derive(Debug, Clone, PartialEq)]
#[allow(clippy::large_enum_variant)]
pub enum PollOutcome {
    Data {
        deltas: Vec<ViewDelta>,
        checkpoint: Option<Checkpoint>,
    },
    Empty {
        checkpoint: Option<Checkpoint>,
    },
    Reset(ResetFrame),
}

/// One result row: stable identity plus projected field values.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ResultItem {
    /// Stable row identity across snapshots and deltas.
    pub identity: String,
    /// Projected canonical JSON bytes, keyed by field path. The SDK keeps
    /// these opaque; a caller may parse a field explicitly at its boundary.
    pub value: BTreeMap<String, Vec<u8>>,
}

/// Authorized snapshot bootstrapping or replacing a view.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ViewSnapshot {
    /// View generation; deltas from another generation force a reset.
    #[serde(alias = "viewGeneration")]
    pub view_generation: u64,
    /// Authorization generation for this complete authorized result.
    #[serde(default)]
    pub authorization_generation: u64,
    /// Revision of this snapshot (the journal cut).
    #[serde(alias = "baseRevision")]
    pub revision: String,
    /// Full ordered result.
    pub items: Vec<ResultItem>,
    /// Lowercase hexadecimal digest stated by the producer.
    #[serde(default)]
    pub digest: String,
    /// Position to resume from.
    pub checkpoint: Checkpoint,
}

/// Incremental change between two revisions (Z-set delta).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ViewDelta {
    /// Generation this delta applies to.
    #[serde(alias = "viewGeneration")]
    pub view_generation: u64,
    /// Revision the delta starts from.
    #[serde(alias = "fromRevision")]
    pub from_revision: String,
    /// Revision the delta ends at.
    #[serde(alias = "toRevision")]
    pub to_revision: String,
    /// Ordered operations.
    pub operations: Vec<ViewOperation>,
    /// Lowercase hexadecimal digest stated for the complete result at the
    /// target revision.
    #[serde(default)]
    pub digest: String,
    /// Position to resume from.
    pub checkpoint: Checkpoint,
}

/// One insert / update / patch / remove / move operation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum ViewOperation {
    Insert {
        identity: String,
        position: u64,
        value: BTreeMap<String, Vec<u8>>,
    },
    Update {
        identity: String,
        value: BTreeMap<String, Vec<u8>>,
    },
    Patch {
        identity: String,
        changes: Vec<FieldChange>,
    },
    Remove {
        identity: String,
    },
    Move {
        identity: String,
        position: u64,
    },
}

/// One projected field replacement or removal in a patch operation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FieldChange {
    pub path: String,
    pub value: Option<Vec<u8>>,
}

/// Outcome of applying a delta to a [`Subscription`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ApplyOutcome {
    Applied,
    Duplicate,
    ResetRequired(ResetReason),
}

/// Client-side view of one live query: snapshot bootstrap plus ordered
/// deltas, with checkpoint resume and typed resets.
#[derive(Debug, Clone)]
pub struct Subscription {
    id: String,
    generation: Option<u64>,
    authorization_generation: Option<u64>,
    revision: Option<String>,
    items: Vec<ResultItem>,
    digest: String,
    checkpoint: Option<Checkpoint>,
    pending: VecDeque<ViewDelta>,
}

impl Subscription {
    /// Create an empty subscription; the first server frame bootstraps it.
    #[must_use]
    pub fn new(id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            generation: None,
            authorization_generation: None,
            revision: None,
            items: Vec::new(),
            digest: String::new(),
            checkpoint: None,
            pending: VecDeque::new(),
        }
    }

    /// Subscription id.
    #[must_use]
    pub fn id(&self) -> &str {
        &self.id
    }

    /// Current items.
    #[must_use]
    pub fn items(&self) -> &[ResultItem] {
        &self.items
    }

    /// Latest installed checkpoint, if any.
    #[must_use]
    pub fn checkpoint(&self) -> Option<&Checkpoint> {
        self.checkpoint.as_ref()
    }

    /// Current immutable journal revision, if a snapshot has been applied.
    #[must_use]
    pub fn revision(&self) -> Option<&str> {
        self.revision.as_deref()
    }

    /// Current authorization generation, if a snapshot has been applied.
    #[must_use]
    pub fn authorization_generation(&self) -> Option<u64> {
        self.authorization_generation
    }

    /// Digest stated by the producer for the complete current result.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    fn install_checkpoint(&mut self, checkpoint: Checkpoint) {
        self.checkpoint = Some(checkpoint);
    }

    /// Install an authorized snapshot (bootstrap or reset).
    pub fn apply_snapshot(&mut self, snapshot: ViewSnapshot) {
        self.generation = Some(snapshot.view_generation);
        self.authorization_generation = Some(snapshot.authorization_generation);
        self.revision = Some(snapshot.revision);
        self.items = snapshot.items;
        self.digest = snapshot.digest;
        self.checkpoint = Some(snapshot.checkpoint);
        self.pending.clear();
    }

    /// Replace the view and report why (typed reset path).
    pub fn reset(&mut self, reason: ResetReason, snapshot: ViewSnapshot) -> ResetReason {
        self.apply_snapshot(snapshot);
        reason
    }

    /// Apply one delta in order. Out-of-order or cross-generation deltas
    /// report [`ApplyOutcome::ResetRequired`]; the caller re-attaches.
    #[must_use]
    pub fn apply_delta(&mut self, delta: ViewDelta) -> ApplyOutcome {
        let Some(generation) = self.generation else {
            return ApplyOutcome::ResetRequired(ResetReason::Reconnect);
        };
        if generation != delta.view_generation {
            return ApplyOutcome::ResetRequired(ResetReason::GenerationChanged);
        }
        let Some(revision) = self.revision.as_deref() else {
            return ApplyOutcome::ResetRequired(ResetReason::Reconnect);
        };

        // Wire revisions are u64 values, but the JSON compatibility surface
        // keeps them as strings. Compare numeric revisions numerically so a
        // valid transition such as 9 -> 10 is not rejected lexicographically.
        // Non-numeric legacy revisions retain their existing lexical ordering.
        match compare_revisions(&delta.to_revision, revision) {
            Ordering::Less => ApplyOutcome::Duplicate,
            Ordering::Equal => {
                if self.digest == delta.digest {
                    ApplyOutcome::Duplicate
                } else {
                    ApplyOutcome::ResetRequired(ResetReason::ViewDiverged)
                }
            }
            Ordering::Greater => {
                if compare_revisions(&delta.from_revision, revision) != Ordering::Equal {
                    return ApplyOutcome::ResetRequired(ResetReason::Reconnect);
                }
                if compare_revisions(&delta.to_revision, &delta.from_revision) != Ordering::Greater
                {
                    return ApplyOutcome::ResetRequired(ResetReason::InvalidOperations);
                }
                // Apply against a candidate so an invalid operation can never
                // partially mutate an otherwise authoritative revision.
                let mut candidate = self.items.clone();
                for operation in &delta.operations {
                    if !apply_operation(&mut candidate, operation) {
                        return ApplyOutcome::ResetRequired(ResetReason::InvalidOperations);
                    }
                }
                self.items = candidate;
                self.revision = Some(delta.to_revision.clone());
                self.digest = delta.digest.clone();
                self.checkpoint = Some(delta.checkpoint.clone());
                ApplyOutcome::Applied
            }
        }
    }
}

fn compare_revisions(left: &str, right: &str) -> Ordering {
    match (left.parse::<u64>(), right.parse::<u64>()) {
        (Ok(left), Ok(right)) => left.cmp(&right),
        _ => match (revision_suffix(left), revision_suffix(right)) {
            (Some((left_prefix, left_number)), Some((right_prefix, right_number)))
                if left_prefix == right_prefix =>
            {
                left_number.cmp(&right_number)
            }
            _ => left.cmp(right),
        },
    }
}

fn revision_suffix(value: &str) -> Option<(&str, u64)> {
    let index = value.find(|character: char| character.is_ascii_digit())?;
    let (prefix, suffix) = value.split_at(index);
    (!suffix.is_empty())
        .then(|| suffix.parse().ok())
        .flatten()
        .map(|number| (prefix, number))
}

fn apply_operation(items: &mut Vec<ResultItem>, operation: &ViewOperation) -> bool {
    match operation {
        ViewOperation::Insert {
            identity,
            position,
            value,
        } => {
            if items.iter().any(|item| &item.identity == identity) {
                return false;
            }
            let index = (*position as usize).min(items.len());
            items.insert(
                index,
                ResultItem {
                    identity: identity.clone(),
                    value: value.clone(),
                },
            );
            true
        }
        ViewOperation::Update { identity, value } => {
            if let Some(item) = items.iter_mut().find(|item| &item.identity == identity) {
                item.value = value.clone();
                true
            } else {
                false
            }
        }
        ViewOperation::Patch { identity, changes } => {
            if let Some(item) = items.iter_mut().find(|item| &item.identity == identity) {
                for change in changes {
                    if let Some(value) = &change.value {
                        item.value.insert(change.path.clone(), value.clone());
                    } else {
                        item.value.remove(&change.path);
                    }
                }
                true
            } else {
                false
            }
        }
        ViewOperation::Remove { identity } => {
            let before = items.len();
            items.retain(|item| &item.identity != identity);
            items.len() != before
        }
        ViewOperation::Move { identity, position } => {
            if let Some(index) = items.iter().position(|item| &item.identity == identity) {
                let item = items.remove(index);
                let target = (*position as usize).min(items.len());
                items.insert(target, item);
                true
            } else {
                false
            }
        }
    }
}

/// Reactive client. Obtain from [`crate::TerraScaleClient::reactive`].
#[derive(Debug, Clone)]
pub struct ReactiveClient {
    shared: SharedClient,
}

impl ReactiveClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    /// Attach to (or create) the canonical shared view for `query` and
    /// return the opening snapshot plus resume checkpoint.
    pub async fn subscribe(
        &self,
        scope: &DatabaseScope,
        query: &Query,
        options: SubscribeOptions,
    ) -> Result<AttachedView> {
        query.validate()?;
        if let Some(checkpoint) = options.checkpoint.as_ref() {
            Checkpoint::try_new(checkpoint.to_bytes())?;
        }
        // Attach is authenticated member traffic. A hint may select a member
        // host, but the route itself is always the canonical API path (there
        // is no project/database reactive route).
        let url = format!(
            "{}/reactive/views",
            self.shared.pinned_base_url(options.region_hint)?
        );
        let subscription = options
            .subscription
            .as_deref()
            .filter(|value| !value.trim().is_empty())
            .unwrap_or("rust-subscription");
        if subscription.len() > 128 {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_subscription",
                "Reactive subscription identities are limited to 128 bytes.",
            ));
        }
        // The wire contract carries canonical named/ordinal parameter bytes.
        // `params()` is only the compatibility positional accessor.
        let parameters = serde_json::to_vec(query.parameters()).map_err(|error| {
            Error::new(
                ErrorKind::Validation,
                "invalid_parameters",
                format!("Reactive parameters are not serializable: {error}"),
            )
        })?;
        let body = serde_json::json!({
            "databaseId": scope.database_id,
            "subscription": subscription,
            "statement": query.sql(),
            "parameters": BASE64.encode(&parameters),
            "checkpoint": options.checkpoint.as_ref().map(Checkpoint::to_base64),
        });
        let mut request = Request::json("reactive.subscribe", Method::Post, url, &body)?;
        if let Some(hint) = options.region_hint {
            request.headers.push((
                "x-terrascale-preferred-region".to_owned(),
                hint.as_header_str().to_owned(),
            ));
        }
        let view: AttachedView = self.shared.send_typed(request).await?;
        Ok(view)
    }

    /// Poll an attached view's immutable CDN log from its opening revision.
    pub async fn poll(&self, view: &AttachedView) -> Result<PollResponse> {
        self.poll_from(view, &view.snapshot.revision).await
    }

    /// Poll an attached view from an explicit immutable revision.
    pub async fn poll_from(&self, view: &AttachedView, offset: &str) -> Result<PollResponse> {
        self.poll_with_cancellation(view, offset, PollOptions::new(1), &CancellationToken::new())
            .await
    }

    /// Poll with a bounded retry budget. The capability is always sent in a
    /// header and the canonical log URL is never rewritten or placed in a
    /// cache key by the client.
    pub async fn poll_with_options(
        &self,
        view: &AttachedView,
        offset: &str,
        options: PollOptions,
    ) -> Result<PollResponse> {
        self.poll_with_cancellation(view, offset, options, &CancellationToken::new())
            .await
    }

    pub async fn poll_with_cancellation(
        &self,
        view: &AttachedView,
        offset: &str,
        options: PollOptions,
        cancellation: &CancellationToken,
    ) -> Result<PollResponse> {
        if cancellation.is_cancelled() {
            return Err(cancelled_error());
        }
        if view.capability.as_deref().is_none_or(str::is_empty) {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "missing_view_capability",
                "The reactive attach response did not include a view capability for the CDN log.",
            ));
        }
        if offset.trim().is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_reactive_offset",
                "A reactive log poll requires a non-empty revision offset.",
            ));
        }

        let attempts = options.max_attempts.max(1);
        let advertised_log_url = if view.log_url.trim().is_empty() {
            canonical_log_path(&view.view_id, view.generation)
        } else {
            view.log_url.as_str().to_owned()
        };
        let log_url = advertised_url(
            &advertised_log_url,
            &canonical_log_path(&view.view_id, view.generation),
        )
        .map_err(|message| {
            Error::new(ErrorKind::Validation, "invalid_reactive_endpoint", message)
        })?;
        let log_url = resolve_delivery(self.shared.config().sync_base_url(), &log_url);
        let url = append_offset(&log_url, offset);
        let mut last_error = None;
        let deadline = options.deadline.min(MAX_POLL_WINDOW);
        let started = Instant::now();
        for attempt in 0..attempts {
            if cancellation.is_cancelled() {
                return Err(cancelled_error());
            }
            let mut request = Request {
                route_name: "reactive.poll".to_owned(),
                method: Method::Get,
                url: url.clone(),
                headers: vec![(SKIP_AUTH_MARKER.to_owned(), String::new())],
                body: None,
            };
            request.headers.push((
                CAPABILITY_HEADER.to_owned(),
                view.capability.clone().expect("capability checked above"),
            ));
            request
                .headers
                .push(("accept".to_owned(), "application/msgpack".to_owned()));
            let remaining = deadline.saturating_sub(started.elapsed());
            if remaining.is_zero() {
                return Err(Error::transport(
                    "reactive poll exceeded its 25-second deadline",
                ));
            }
            let result = tokio::select! {
                _ = cancellation.cancelled() => return Err(cancelled_error()),
                result = tokio::time::timeout(remaining, self.shared.send_typed::<PollResponse>(request)) => {
                    match result {
                        Ok(result) => result,
                        Err(_) => Err(Error::transport("reactive poll exceeded its 25-second deadline")),
                    }
                },
            };
            match result.map_err(normalize_msgpack_error) {
                Ok(response) => return Ok(response),
                Err(error) if is_retryable(&error) && attempt + 1 < attempts => {
                    last_error = Some(error);
                    tokio::task::yield_now().await;
                }
                Err(error) => return Err(error),
            }
        }
        Err(last_error.unwrap_or_else(|| Error::transport("reactive poll exhausted")))
    }

    /// Attach and create a local state machine for consuming the CDN log.
    pub async fn subscribe_live(
        &self,
        scope: &DatabaseScope,
        query: &Query,
        options: SubscribeOptions,
    ) -> Result<LiveSubscription> {
        let websocket = options.transport == Transport::WebSocket;
        let view = self.subscribe(scope, query, options.clone()).await?;
        if view.log_url.trim().is_empty() {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "missing_log_url",
                "The reactive attach response did not include its immutable log URL.",
            ));
        }
        let mut state = Subscription::new(view.view_id.clone());
        if view.snapshot_urls.is_empty() {
            // A resume attach may legitimately return only checkpoint + offset;
            // the local state is already represented by that position.
            state.apply_snapshot(view.snapshot.clone());
        } else {
            // A chunk URL is an immutable, authorized MessagePack snapshot
            // envelope. Fetch every advertised URL before exposing the live
            // state; an attach must never seed a fabricated revision zero.
            let snapshot = self.load_snapshot_chunks(&view).await?;
            state.apply_snapshot(snapshot);
        }
        let socket = if websocket {
            let parameters = serde_json::to_vec(query.parameters()).map_err(|error| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_parameters",
                    format!("Reactive parameters are not serializable: {error}"),
                )
            })?;
            Some(
                crate::websocket::open(crate::websocket::OpenRequest {
                    base_url: &self.shared.pinned_base_url(options.region_hint)?,
                    view: &view.view_id,
                    generation: view.generation,
                    capability: view
                        .capability
                        .as_deref()
                        .expect("attach validates capability"),
                    subscription: options
                        .subscription
                        .as_deref()
                        .filter(|v| !v.trim().is_empty())
                        .unwrap_or("rust-subscription"),
                    query: query.sql(),
                    parameters: &parameters,
                    checkpoint: Some(&view.checkpoint),
                    bearer: self.shared.api_key(),
                })
                .await?,
            )
        } else {
            None
        };
        Ok(LiveSubscription {
            client: self.clone(),
            view,
            state,
            options: PollOptions::default(),
            cancellation: CancellationToken::new(),
            socket,
        })
    }

    async fn load_snapshot_chunks(&self, view: &AttachedView) -> Result<ViewSnapshot> {
        if view.snapshot_urls.is_empty() || view.snapshot_urls.len() > 1024 {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "invalid_snapshot_chunks",
                "A reactive view must advertise between 1 and 1024 snapshot chunks.",
            ));
        }
        let mut assembled: Option<ViewSnapshot> = None;
        for (index, advertised) in view.snapshot_urls.iter().enumerate() {
            let mut request = Request {
                route_name: "reactive.snapshot".to_owned(),
                method: Method::Get,
                url: {
                    let expected = canonical_snapshot_path(&view.view_id, view.generation, index);
                    let validated = advertised_url(advertised, &expected).map_err(|message| {
                        Error::new(ErrorKind::Validation, "invalid_reactive_endpoint", message)
                    })?;
                    resolve_delivery(self.shared.config().sync_base_url(), &validated)
                },
                headers: vec![
                    (SKIP_AUTH_MARKER.to_owned(), String::new()),
                    ("accept".to_owned(), "application/msgpack".to_owned()),
                ],
                body: None,
            };
            request.headers.push((
                CAPABILITY_HEADER.to_owned(),
                view.capability.clone().ok_or_else(|| {
                    Error::new(
                        ErrorKind::InvalidResponse,
                        "missing_view_capability",
                        "The reactive attach response did not include a view capability.",
                    )
                })?,
            ));
            let response = self.shared.send_typed::<PollResponse>(request).await?;
            let chunk = response
                .reset
                .and_then(|reset| reset.snapshot)
                .ok_or_else(|| {
                    Error::new(
                        ErrorKind::InvalidResponse,
                        "invalid_snapshot_chunk",
                        "A reactive snapshot URL did not return a complete snapshot frame.",
                    )
                })?;
            Checkpoint::try_new(chunk.checkpoint.to_bytes())?;
            if let Some(current) = &mut assembled {
                if current.view_generation != chunk.view_generation
                    || current.revision != chunk.revision
                    || current.checkpoint != chunk.checkpoint
                {
                    return Err(Error::new(
                        ErrorKind::InvalidResponse,
                        "inconsistent_snapshot_chunks",
                        "Reactive snapshot chunks disagree on generation, revision, or checkpoint.",
                    ));
                }
                for item in chunk.items {
                    if current
                        .items
                        .iter()
                        .any(|existing| existing.identity == item.identity)
                    {
                        return Err(Error::new(
                            ErrorKind::InvalidResponse,
                            "duplicate_snapshot_identity",
                            "Reactive snapshot chunks contain a duplicate identity.",
                        ));
                    }
                    current.items.push(item);
                }
                if current.items.len() > 10_000 {
                    return Err(Error::new(
                        ErrorKind::InvalidResponse,
                        "oversized_snapshot",
                        "Reactive snapshot chunks exceed the 10000-item bound.",
                    ));
                }
            } else {
                assembled = Some(chunk);
            }
        }
        assembled.ok_or_else(|| Error::invalid_response("reactive snapshot chunks were empty"))
    }

    /// Execute a live query once and return its rows ("subscribe once").
    pub async fn execute(&self, scope: &DatabaseScope, query: &Query) -> Result<SqlRows> {
        query.validate()?;
        let url = format!("{}/sql", self.shared.database_root(scope)?);
        let body = serde_json::json!({
            "sql": query.sql(),
            "parameters": query.parameters(),
        });
        let request = Request::json("sql.execute", Method::Post, url, &body)?;
        self.shared.send_typed(request).await
    }
}

/// View attachment returned by [`ReactiveClient::subscribe`].
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct AttachedView {
    /// Opaque view id.
    #[serde(alias = "viewId")]
    pub view_id: String,
    /// View generation.
    pub generation: u64,
    /// Opening snapshot.
    pub snapshot: ViewSnapshot,
    /// Resume checkpoint (also inside the snapshot).
    pub checkpoint: Checkpoint,
    /// Immutable snapshot chunk URLs, served from the CDN.
    #[serde(default, alias = "snapshotUrls", alias = "snapshot_urls")]
    pub snapshot_urls: Vec<String>,
    /// Immutable CDN log URL. The client appends only the `offset` query.
    #[serde(default, alias = "logUrl", alias = "log_url")]
    pub log_url: String,
    /// Capability header value for `sync.api.` reads (never in URLs).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub capability: Option<String>,
}

impl<'de> Deserialize<'de> for AttachedView {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let value = serde_json::Value::deserialize(deserializer)?;
        decode_attached_view(value).map_err(serde::de::Error::custom)
    }
}

fn decode_attached_view(value: serde_json::Value) -> std::result::Result<AttachedView, String> {
    let object = value
        .as_object()
        .ok_or_else(|| "reactive attach response is not an object".to_owned())?;
    let view_id = string_field(object, &["viewId", "view_id", "view"])
        .ok_or_else(|| "reactive attach response has no view id".to_owned())?;
    if view_id.is_empty() || view_id.len() > 128 {
        return Err("reactive attach response has an invalid view id".to_owned());
    }
    let generation = u64_field(object, &["generation", "view_generation", "viewGeneration"])
        .ok_or_else(|| "reactive attach response has no valid generation".to_owned())?;
    let capability = string_field(object, &["capability", "viewCapability", "view_capability"])
        .filter(|value| !value.is_empty())
        .ok_or_else(|| "reactive attach response has no view capability".to_owned())?;
    if let Some(header) = string_field(object, &["capability_header", "capabilityHeader"]) {
        if header != CAPABILITY_HEADER {
            return Err(format!(
                "reactive attach response advertises unsupported capability header {header}"
            ));
        }
    }
    let checkpoint = object
        .get("checkpoint")
        .ok_or_else(|| "reactive attach response has no checkpoint".to_owned())
        .and_then(|value| decode_checkpoint_value(value, "checkpoint"))?;

    let raw_urls = object
        .get("snapshot_chunks")
        .or_else(|| object.get("snapshotChunks"))
        .or_else(|| object.get("snapshotUrls"))
        .or_else(|| object.get("snapshot_urls"))
        .or_else(|| object.get("snapshotChunkUrls"))
        .or_else(|| object.get("snapshot_chunk_urls"));
    let snapshot_urls = match raw_urls {
        None => Vec::new(),
        Some(value) => {
            let values = value
                .as_array()
                .ok_or_else(|| "reactive snapshot URLs are not an array".to_owned())?;
            if values.len() > 1024 {
                return Err("reactive snapshot URL count exceeds 1024".to_owned());
            }
            values
                .iter()
                .enumerate()
                .map(|(index, value)| {
                    let advertised = value
                        .as_str()
                        .ok_or_else(|| format!("reactive snapshot URL {index} is not a string"))?;
                    if advertised.trim().is_empty() {
                        return Err(format!("reactive snapshot URL {index} is not a string"));
                    }
                    advertised_url(
                        advertised,
                        &canonical_snapshot_path(&view_id, generation, index),
                    )
                })
                .collect::<std::result::Result<Vec<_>, _>>()?
        }
    };
    let snapshot = object
        .get("snapshot")
        .map(|value| decode_snapshot_value(value, Some(&checkpoint), generation))
        .transpose()?
        .unwrap_or_else(|| {
            let offset = object
                .get("offset")
                .and_then(value_string)
                .unwrap_or_else(|| "0".to_owned());
            empty_snapshot_at(generation, checkpoint.clone(), offset)
        });
    if object.get("snapshot").is_none() && snapshot_urls.is_empty() {
        // A checkpoint-only attach is the normal resume response. Its offset
        // is the state position; no synthetic revision is accepted.
        if object.get("offset").and_then(value_string).is_none() {
            return Err("checkpoint-only reactive attach has no offset".to_owned());
        }
    }
    if snapshot.view_generation != generation {
        return Err("reactive snapshot generation does not match attach generation".to_owned());
    }
    if snapshot.checkpoint != checkpoint {
        return Err("reactive attach checkpoint disagrees with snapshot".to_owned());
    }
    let log_url = object
        .get("log")
        .or_else(|| object.get("logUrl"))
        .or_else(|| object.get("log_url"))
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| "reactive attach response has no log URL".to_owned())?;
    let log_url = advertised_url(log_url, &canonical_log_path(&view_id, generation))?;
    Ok(AttachedView {
        view_id: view_id.clone(),
        generation,
        snapshot,
        checkpoint,
        snapshot_urls,
        log_url,
        capability: Some(capability),
    })
}

fn value_string(value: &serde_json::Value) -> Option<String> {
    match value {
        serde_json::Value::String(value) if !value.trim().is_empty() => Some(value.clone()),
        serde_json::Value::Number(value) => Some(value.to_string()),
        _ => None,
    }
}

fn string_field(
    object: &serde_json::Map<String, serde_json::Value>,
    names: &[&str],
) -> Option<String> {
    names
        .iter()
        .find_map(|name| object.get(*name)?.as_str().map(str::to_owned))
}

fn u64_field(object: &serde_json::Map<String, serde_json::Value>, names: &[&str]) -> Option<u64> {
    names.iter().find_map(|name| match object.get(*name)? {
        serde_json::Value::Number(value) => value.as_u64(),
        serde_json::Value::String(value) => value.parse().ok(),
        _ => None,
    })
}

fn decode_checkpoint_value(
    value: &serde_json::Value,
    field: &str,
) -> std::result::Result<Checkpoint, String> {
    let bytes = match value {
        serde_json::Value::Array(values) => values
            .iter()
            .map(|value| {
                value
                    .as_u64()
                    .filter(|byte| *byte <= 255)
                    .map(|byte| byte as u8)
            })
            .collect::<Option<Vec<_>>>(),
        serde_json::Value::String(encoded) => BASE64.decode(encoded).ok(),
        _ => None,
    }
    .ok_or_else(|| format!("attach response field '{field}' is not bytes"))?;
    Checkpoint::try_new(bytes).map_err(|error| error.message)
}

fn empty_snapshot_at(generation: u64, checkpoint: Checkpoint, revision: String) -> ViewSnapshot {
    ViewSnapshot {
        view_generation: generation,
        authorization_generation: 0,
        revision,
        items: Vec::new(),
        digest: String::new(),
        checkpoint,
    }
}

fn decode_snapshot_value(
    value: &serde_json::Value,
    outer_checkpoint: Option<&Checkpoint>,
    generation: u64,
) -> std::result::Result<ViewSnapshot, String> {
    let object = value
        .as_object()
        .ok_or_else(|| "reactive inline snapshot is not an object".to_owned())?;
    let view_generation =
        u64_field(object, &["view_generation", "viewGeneration"]).unwrap_or(generation);
    let authorization_generation = u64_field(
        object,
        &["authorization_generation", "authorizationGeneration"],
    )
    .unwrap_or_default();
    let revision = match object.get("revision") {
        Some(serde_json::Value::String(value)) => value.clone(),
        Some(serde_json::Value::Number(value)) => value.to_string(),
        _ => "0".to_owned(),
    };
    let digest = object
        .get("digest")
        .and_then(serde_json::Value::as_str)
        .unwrap_or_default()
        .to_owned();
    let inner_checkpoint = object
        .get("checkpoint")
        .map(|value| decode_checkpoint_value(value, "snapshot.checkpoint"))
        .transpose()?;
    if let (Some(outer), Some(inner)) = (outer_checkpoint, inner_checkpoint.as_ref()) {
        if outer != inner {
            return Err("reactive snapshot checkpoint disagrees with attach checkpoint".to_owned());
        }
    }
    let checkpoint = outer_checkpoint
        .cloned()
        .or(inner_checkpoint)
        .ok_or_else(|| "reactive snapshot has no checkpoint".to_owned())?;
    let items = object
        .get("items")
        .and_then(serde_json::Value::as_array)
        .ok_or_else(|| "reactive snapshot items are not an array".to_owned())?
        .iter()
        .map(decode_result_item)
        .collect::<std::result::Result<Vec<_>, _>>()?;
    Ok(ViewSnapshot {
        view_generation,
        authorization_generation,
        revision,
        items,
        digest,
        checkpoint,
    })
}

fn decode_result_item(value: &serde_json::Value) -> std::result::Result<ResultItem, String> {
    let object = value
        .as_object()
        .ok_or_else(|| "reactive result item is not an object".to_owned())?;
    let identity = object
        .get("identity")
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| "reactive result item has no identity".to_owned())?
        .to_owned();
    let mut result = BTreeMap::new();
    if let Some(value) = object.get("value").and_then(serde_json::Value::as_object) {
        // Legacy JSON snapshots expose values as JSON. Preserve them as
        // canonical bytes at the boundary; MessagePack never takes this
        // branch and therefore never parses/transcodes its opaque bytes.
        for (path, value) in value {
            if result
                .insert(
                    path.clone(),
                    serde_json::to_vec(value).map_err(|error| error.to_string())?,
                )
                .is_some()
            {
                return Err("reactive result item contains duplicate fields".to_owned());
            }
        }
    } else {
        let fields = object
            .get("fields")
            .and_then(serde_json::Value::as_array)
            .ok_or_else(|| "reactive result item fields are not an array".to_owned())?;
        for field in fields {
            let field = field
                .as_object()
                .ok_or_else(|| "reactive projected field is not an object".to_owned())?;
            let path = field
                .get("path")
                .and_then(serde_json::Value::as_str)
                .ok_or_else(|| "reactive projected field has no path".to_owned())?;
            let bytes = decode_bytes_value(
                field
                    .get("value")
                    .ok_or_else(|| "reactive projected field has no value".to_owned())?,
            )?;
            if result.insert(path.to_owned(), bytes).is_some() {
                return Err("reactive result item contains duplicate fields".to_owned());
            }
        }
    }
    Ok(ResultItem {
        identity,
        value: result,
    })
}

fn decode_bytes_value(value: &serde_json::Value) -> std::result::Result<Vec<u8>, String> {
    match value {
        serde_json::Value::Array(values) => values
            .iter()
            .map(|value| {
                value
                    .as_u64()
                    .filter(|byte| *byte <= 255)
                    .map(|byte| byte as u8)
            })
            .collect::<Option<Vec<_>>>()
            .ok_or_else(|| "reactive bytes contain a non-byte".to_owned()),
        serde_json::Value::String(value) => BASE64
            .decode(value)
            .map_err(|error| format!("reactive bytes are not base64: {error}")),
        _ => Err("reactive bytes are not an array or base64 string".to_owned()),
    }
}

impl AttachedView {
    /// Canonical immutable snapshot chunk URLs returned by the member.
    #[must_use]
    pub fn snapshot_urls(&self) -> &[String] {
        &self.snapshot_urls
    }

    /// Canonical immutable log URL returned by the member.
    #[must_use]
    pub fn log_url(&self) -> &str {
        &self.log_url
    }
}

/// A stateful HTTP subscription. Each call to [`Self::next`] performs one
/// bounded long-poll sequence and applies deltas atomically to its view.
#[derive(Debug)]
pub struct LiveSubscription {
    client: ReactiveClient,
    view: AttachedView,
    state: Subscription,
    options: PollOptions,
    cancellation: CancellationToken,
    socket: Option<crate::websocket::Session>,
}

impl LiveSubscription {
    /// Attached view metadata, including immutable CDN URLs and capability.
    #[must_use]
    pub fn view(&self) -> &AttachedView {
        &self.view
    }

    /// Current authorized result state.
    #[must_use]
    pub fn subscription(&self) -> &Subscription {
        &self.state
    }

    /// Set the bounded request-attempt budget for subsequent polls.
    pub fn set_poll_options(&mut self, options: PollOptions) {
        self.options = options;
    }

    /// Request cancellation. The current HTTP future is dropped and no
    /// further retry is made.
    pub fn cancel(&self) {
        self.cancellation.cancel();
    }

    /// Whether this subscription has been cancelled.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.cancellation.is_cancelled()
    }

    /// Poll and apply one immutable log response.
    pub async fn next(&mut self) -> Result<PollOutcome> {
        if self.cancellation.is_cancelled() {
            if let Some(socket) = self.socket.as_mut() {
                socket.close().await;
            }
            return Err(cancelled_error());
        }
        let offset = self
            .state
            .revision()
            .unwrap_or(self.view.snapshot.revision.as_str());
        let response = if let Some(socket) = self.socket.as_mut() {
            let bytes = match socket.next(&self.cancellation).await {
                Ok(bytes) => bytes,
                Err(error) if error.code == "slow_consumer" => {
                    return Ok(PollOutcome::Reset(ResetFrame {
                        reason: ResetReason::Backpressure,
                        snapshot: None,
                        checkpoint: self.state.checkpoint().cloned(),
                        region: None,
                        authorization_generation: None,
                        removed: Vec::new(),
                        digest: String::new(),
                        detail: Some(error.message),
                    }));
                }
                Err(error) => return Err(error),
            };
            let value = decode_reactive_msgpack_json(&bytes)?;
            serde_json::from_value(value).map_err(|error| {
                Error::invalid_response(format!(
                    "WebSocket frame does not match reactive response: {error}"
                ))
            })?
        } else {
            match self
                .client
                .poll_with_cancellation(&self.view, offset, self.options, &self.cancellation)
                .await
            {
                Ok(response) => response,
                Err(error) if error.kind == ErrorKind::RegionRedirect => {
                    return Ok(PollOutcome::Reset(ResetFrame {
                        reason: ResetReason::RegionMoved,
                        snapshot: None,
                        checkpoint: self.state.checkpoint().cloned(),
                        region: error
                            .placement
                            .as_deref()
                            .and_then(|placement| placement.parse().ok()),
                        authorization_generation: None,
                        removed: Vec::new(),
                        digest: String::new(),
                        detail: Some(error.message),
                    }));
                }
                Err(error) if http_reset_reason(&error).is_some() => {
                    let reason = http_reset_reason(&error).expect("checked above");
                    return Ok(PollOutcome::Reset(ResetFrame {
                        reason,
                        snapshot: None,
                        checkpoint: self.state.checkpoint().cloned(),
                        region: None,
                        authorization_generation: None,
                        removed: Vec::new(),
                        digest: String::new(),
                        detail: Some(error.message),
                    }));
                }
                Err(error) if is_retryable(&error) => {
                    return Ok(PollOutcome::Reset(ResetFrame {
                        reason: ResetReason::Reconnect,
                        snapshot: None,
                        checkpoint: self.state.checkpoint().cloned(),
                        region: None,
                        authorization_generation: None,
                        removed: Vec::new(),
                        digest: String::new(),
                        detail: Some(error.message),
                    }));
                }
                Err(error) => return Err(error),
            }
        };
        if let Some(mut reset) = response.reset {
            if reset.checkpoint.is_none() {
                reset.checkpoint = self.state.checkpoint().cloned();
            }
            if let Some(snapshot) = reset.snapshot.clone() {
                if let Some(auth_generation) = reset.authorization_generation {
                    if self
                        .state
                        .authorization_generation()
                        .is_some_and(|current| auth_generation <= current)
                        || snapshot.authorization_generation != auth_generation
                        || snapshot.items.iter().any(|item| {
                            reset
                                .removed
                                .iter()
                                .any(|removed| removed == &item.identity)
                        })
                    {
                        return Ok(PollOutcome::Reset(ResetFrame {
                            reason: ResetReason::ViewDiverged,
                            snapshot: None,
                            checkpoint: self.state.checkpoint().cloned(),
                            region: None,
                            authorization_generation: Some(auth_generation),
                            removed: reset.removed,
                            digest: reset.digest,
                            detail: Some(
                                "authorization replacement disagrees with its removed identities or generation."
                                    .to_owned(),
                            ),
                        }));
                    }
                }
                self.state.apply_snapshot(snapshot);
            }
            return Ok(PollOutcome::Reset(reset));
        }

        let mut next_state = self.state.clone();
        for delta in &response.deltas {
            match next_state.apply_delta(delta.clone()) {
                ApplyOutcome::Applied | ApplyOutcome::Duplicate => {}
                ApplyOutcome::ResetRequired(reason) => {
                    return Ok(PollOutcome::Reset(ResetFrame {
                        reason,
                        snapshot: None,
                        checkpoint: self.state.checkpoint().cloned(),
                        region: None,
                        authorization_generation: None,
                        removed: Vec::new(),
                        digest: String::new(),
                        detail: Some(
                            "The immutable log did not form a contiguous view.".to_owned(),
                        ),
                    }));
                }
            }
        }
        if let Some(checkpoint) = response.checkpoint.clone() {
            next_state.install_checkpoint(checkpoint);
        }
        self.state = next_state;
        if response.deltas.is_empty() {
            Ok(PollOutcome::Empty {
                checkpoint: response.checkpoint,
            })
        } else {
            Ok(PollOutcome::Data {
                deltas: response.deltas,
                checkpoint: response.checkpoint,
            })
        }
    }
}

/// Header carrying the short-lived authorization-equivalent view capability.
pub const CAPABILITY_HEADER: &str = "X-TerraScale-Capability";

pub(crate) const MSGPACK_MAX_MESSAGE_BYTES: usize = 8 * 1024 * 1024;
const MSGPACK_MAX_STRING_BYTES: usize = 262_144;
const MSGPACK_MAX_BINARY_BYTES: usize = 1_048_576;
const MSGPACK_MAX_ARRAY_ITEMS: usize = 10_000;
const MSGPACK_MAX_DEPTH: usize = 64;

/// Decode one strict manifest envelope to the legacy-compatible poll shape.
/// The MessagePack bytes are scanned and bounds-checked before any public
/// values are allocated. This function is public only for the transport seam.
pub(crate) fn decode_reactive_msgpack_json(bytes: &[u8]) -> Result<serde_json::Value> {
    if bytes.len() > MSGPACK_MAX_MESSAGE_BYTES {
        return Err(Error::invalid_response(
            "reactive MessagePack frame exceeds 8 MiB",
        ));
    }
    let mut cursor = 0;
    scan_msgpack(bytes, &mut cursor, 0)?;
    if cursor != bytes.len() {
        return Err(Error::invalid_response(
            "reactive MessagePack frame has trailing bytes",
        ));
    }
    let mut reader = MsgpackReader { bytes, cursor: 0 };
    let envelope_count = reader.array_count(MSGPACK_MAX_ARRAY_ITEMS)?;
    // The published HTTP log and WebSocket protocol carry exactly one
    // indexed envelope per binary frame. An outer array of envelopes is an
    // old/noncanonical transport shape and must fail closed; accepting it
    // would let callers observe a partial batch as one revision.
    if envelope_count != 7 {
        return Err(msgpack_error("noncanonical top-level envelope batch"));
    }
    reader.cursor = 0;
    let response = decode_reactive_envelope(&mut reader)?;
    if reader.cursor != bytes.len() {
        return Err(msgpack_error("trailing bytes after reactive envelope"));
    }
    Ok(response)
}

fn decode_reactive_envelope(reader: &mut MsgpackReader<'_>) -> Result<serde_json::Value> {
    reader.array(7)?;
    if reader.u32()? != 1 || reader.u32()? != 0 {
        return Err(Error::new(
            ErrorKind::InvalidResponse,
            "unsupported_version",
            "unsupported reactive MessagePack protocol version",
        ));
    }
    if reader.u32()? != 0 {
        return Err(Error::invalid_response(
            "reactive MessagePack frame requires unsupported features",
        ));
    }
    let tag = reader.u32()?;
    let _correlation = reader.u64()?;
    let _causation = reader.u64()?;
    if matches!(tag, 1 | 2 | 7) {
        return Err(msgpack_error(
            "received a client-to-server reactive message",
        ));
    }
    decode_msgpack_message(reader, tag)
}

fn msgpack_error(message: impl Into<String>) -> Error {
    Error::new(
        ErrorKind::InvalidResponse,
        "malformed_frame",
        format!("malformed reactive MessagePack frame: {}", message.into()),
    )
}

fn scan_msgpack(bytes: &[u8], cursor: &mut usize, depth: usize) -> Result<()> {
    if depth > MSGPACK_MAX_DEPTH {
        return Err(msgpack_error("maximum nesting depth exceeded"));
    }
    let tag = *bytes
        .get(*cursor)
        .ok_or_else(|| msgpack_error("truncated value"))?;
    *cursor += 1;
    let take = |cursor: &mut usize, count: usize| -> Result<()> {
        let end = cursor
            .checked_add(count)
            .ok_or_else(|| msgpack_error("length overflow"))?;
        if end > bytes.len() {
            return Err(msgpack_error("truncated value"));
        }
        *cursor = end;
        Ok(())
    };
    match tag {
        0x00..=0x7f | 0xc0 | 0xc2 | 0xc3 | 0xe0..=0xff => Ok(()),
        0xca => Err(msgpack_error("float32 is forbidden")),
        0xcb => take(cursor, 8),
        0xcc => take(cursor, 1),
        0xcd => take(cursor, 2),
        0xce => take(cursor, 4),
        0xcf => take(cursor, 8),
        0xd0 => take(cursor, 1),
        0xd1 => take(cursor, 2),
        0xd2 => take(cursor, 4),
        0xd3 => take(cursor, 8),
        0xc4..=0xc6 => {
            let width = match tag {
                0xc4 => 1,
                0xc5 => 2,
                _ => 4,
            };
            let len = read_length(bytes, cursor, width)?;
            if len > MSGPACK_MAX_BINARY_BYTES {
                return Err(msgpack_error("binary field exceeds its bound"));
            }
            take(cursor, len)
        }
        0xa0..=0xbf => {
            let len = usize::from(tag - 0xa0);
            if len > MSGPACK_MAX_STRING_BYTES {
                return Err(msgpack_error("string exceeds its bound"));
            }
            take(cursor, len)
        }
        0xd9..=0xdb => {
            let width = match tag {
                0xd9 => 1,
                0xda => 2,
                _ => 4,
            };
            let len = read_length(bytes, cursor, width)?;
            if len > MSGPACK_MAX_STRING_BYTES {
                return Err(msgpack_error("string exceeds its bound"));
            }
            take(cursor, len)
        }
        0x90..=0x9f => {
            let count = usize::from(tag - 0x90);
            for _ in 0..count {
                scan_msgpack(bytes, cursor, depth + 1)?;
            }
            Ok(())
        }
        0xdc | 0xdd => {
            let width = if tag == 0xdc { 2 } else { 4 };
            let count = read_length(bytes, cursor, width)?;
            if count > MSGPACK_MAX_ARRAY_ITEMS {
                return Err(msgpack_error("array exceeds its bound"));
            }
            for _ in 0..count {
                scan_msgpack(bytes, cursor, depth + 1)?;
            }
            Ok(())
        }
        0x80..=0x8f | 0xde | 0xdf => {
            Err(msgpack_error("maps are forbidden by the indexed contract"))
        }
        0xc1 => Err(msgpack_error("reserved MessagePack type")),
        0xc7..=0xc9 | 0xd4..=0xd8 => Err(msgpack_error("extension types are forbidden")),
    }
}

fn read_length(bytes: &[u8], cursor: &mut usize, width: usize) -> Result<usize> {
    let end = cursor
        .checked_add(width)
        .ok_or_else(|| msgpack_error("length overflow"))?;
    if end > bytes.len() {
        return Err(msgpack_error("truncated length"));
    }
    let mut value = 0usize;
    for byte in &bytes[*cursor..end] {
        value = value
            .checked_mul(256)
            .and_then(|v| v.checked_add(usize::from(*byte)))
            .ok_or_else(|| msgpack_error("length overflow"))?;
    }
    *cursor = end;
    Ok(value)
}

struct MsgpackReader<'a> {
    bytes: &'a [u8],
    cursor: usize,
}

impl<'a> MsgpackReader<'a> {
    fn byte(&mut self) -> Result<u8> {
        let byte = *self
            .bytes
            .get(self.cursor)
            .ok_or_else(|| msgpack_error("truncated value"))?;
        self.cursor += 1;
        Ok(byte)
    }
    fn take(&mut self, count: usize) -> Result<&'a [u8]> {
        let end = self
            .cursor
            .checked_add(count)
            .ok_or_else(|| msgpack_error("length overflow"))?;
        if end > self.bytes.len() {
            return Err(msgpack_error("truncated value"));
        }
        let result = &self.bytes[self.cursor..end];
        self.cursor = end;
        Ok(result)
    }
    fn array(&mut self, expected: usize) -> Result<()> {
        let tag = self.byte()?;
        let count = match tag {
            0x90..=0x9f => usize::from(tag - 0x90),
            0xdc => usize::from(u16::from_be_bytes(self.take(2)?.try_into().unwrap())),
            0xdd => usize::try_from(u32::from_be_bytes(self.take(4)?.try_into().unwrap()))
                .map_err(|_| msgpack_error("array length overflow"))?,
            _ => return Err(msgpack_error("expected indexed array")),
        };
        if count != expected || count > MSGPACK_MAX_ARRAY_ITEMS {
            return Err(msgpack_error("indexed array has the wrong length"));
        }
        Ok(())
    }
    fn u64(&mut self) -> Result<u64> {
        let tag = self.byte()?;
        match tag {
            0..=0x7f => Ok(u64::from(tag)),
            0xcc => Ok(u64::from(self.byte()?)),
            0xcd => Ok(u64::from(u16::from_be_bytes(
                self.take(2)?.try_into().unwrap(),
            ))),
            0xce => Ok(u64::from(u32::from_be_bytes(
                self.take(4)?.try_into().unwrap(),
            ))),
            0xcf => Ok(u64::from_be_bytes(self.take(8)?.try_into().unwrap())),
            _ => Err(msgpack_error("expected unsigned integer")),
        }
    }
    fn u32(&mut self) -> Result<u32> {
        let value = self.u64()?;
        u32::try_from(value).map_err(|_| msgpack_error("integer exceeds u32"))
    }
    fn text(&mut self, max: usize) -> Result<String> {
        let tag = self.byte()?;
        let len = match tag {
            0xa0..=0xbf => usize::from(tag - 0xa0),
            0xd9 => usize::from(self.byte()?),
            0xda => usize::from(u16::from_be_bytes(self.take(2)?.try_into().unwrap())),
            0xdb => usize::try_from(u32::from_be_bytes(self.take(4)?.try_into().unwrap()))
                .map_err(|_| msgpack_error("string length overflow"))?,
            _ => return Err(msgpack_error("expected text")),
        };
        if len > max || len > MSGPACK_MAX_STRING_BYTES {
            return Err(msgpack_error("string exceeds its bound"));
        }
        String::from_utf8(self.take(len)?.to_vec()).map_err(|_| msgpack_error("invalid UTF-8"))
    }
    fn binary(&mut self, min: usize, max: usize) -> Result<Vec<u8>> {
        let tag = self.byte()?;
        let width = match tag {
            0xc4 => 1,
            0xc5 => 2,
            0xc6 => 4,
            _ => return Err(msgpack_error("expected binary")),
        };
        let len = read_length(self.bytes, &mut self.cursor, width)?;
        if len < min || len > max || len > MSGPACK_MAX_BINARY_BYTES {
            return Err(msgpack_error("binary exceeds its bound"));
        }
        Ok(self.take(len)?.to_vec())
    }
    fn nil(&mut self) -> Result<()> {
        if self.byte()? == 0xc0 {
            Ok(())
        } else {
            Err(msgpack_error("expected nil"))
        }
    }
}

fn decode_msgpack_message(reader: &mut MsgpackReader<'_>, tag: u32) -> Result<serde_json::Value> {
    match tag {
        3 => {
            // snapshot
            reader.array(2)?;
            let _subscription = reader.text(128)?;
            let snapshot = decode_msgpack_snapshot(reader)?;
            let authorization_generation = snapshot
                .get("authorization_generation")
                .cloned()
                .unwrap_or(serde_json::Value::from(0));
            let digest = snapshot
                .get("digest")
                .cloned()
                .unwrap_or(serde_json::Value::from(""));
            Ok(
                serde_json::json!({"reset": {"reason": "reconnect", "authorization_generation": authorization_generation, "digest": digest, "snapshot": snapshot}}),
            )
        }
        4 => {
            // delta
            reader.array(7)?;
            let _subscription = reader.text(128)?;
            let generation = reader.u64()?;
            let from = reader.u64()?;
            let to = reader.u64()?;
            let operations = decode_msgpack_operations(reader)?;
            let digest = reader.text(64)?;
            let checkpoint = BASE64.encode(reader.binary(1, MAX_CHECKPOINT_BYTES)?);
            Ok(
                serde_json::json!({"deltas": [{"view_generation": generation, "from_revision": from.to_string(), "to_revision": to.to_string(), "operations": operations, "digest": digest, "checkpoint": checkpoint}]}),
            )
        }
        5 => {
            // reset
            reader.array(3)?;
            let _subscription = reader.text(128)?;
            let reason = decode_reset_reason(reader.u32()?)?;
            let snapshot = decode_msgpack_snapshot(reader)?;
            let authorization_generation = snapshot
                .get("authorization_generation")
                .cloned()
                .unwrap_or(serde_json::Value::from(0));
            let digest = snapshot
                .get("digest")
                .cloned()
                .unwrap_or(serde_json::Value::from(""));
            Ok(
                serde_json::json!({"reset": {"reason": reason, "authorization_generation": authorization_generation, "digest": digest, "snapshot": snapshot}}),
            )
        }
        6 => {
            reader.array(0)?;
            Ok(serde_json::json!({}))
        }
        7 => {
            // checkpoint acknowledgement advances the opaque resume token
            // without carrying a data-bearing delta.
            reader.array(2)?;
            let _subscription = reader.text(128)?;
            let checkpoint = BASE64.encode(reader.binary(1, MAX_CHECKPOINT_BYTES)?);
            Ok(serde_json::json!({"checkpoint": checkpoint}))
        }
        8 => {
            // authorization changed is a typed permission reset
            reader.array(4)?;
            let _subscription = reader.text(128)?;
            let authorization_generation = reader.u64()?;
            let count = reader.array_count(10_000)?;
            let mut removed = Vec::with_capacity(count);
            for _ in 0..count {
                removed.push(reader.text(256)?);
            }
            let snapshot = decode_msgpack_snapshot(reader)?;
            let digest = snapshot
                .get("digest")
                .and_then(serde_json::Value::as_str)
                .unwrap_or_default()
                .to_owned();
            Ok(
                serde_json::json!({"reset": {"reason": "permission_changed", "authorization_generation": authorization_generation, "removed": removed, "digest": digest, "snapshot": snapshot}}),
            )
        }
        9 => {
            reader.array(1)?;
            let region = reader.text(64)?;
            Ok(serde_json::json!({"reset": {"reason": "region_moved", "region": region}}))
        }
        10 => {
            // errors are intentionally typed, never silently retried
            reader.array(2)?;
            let code = reader.u32()?;
            let detail = if reader.bytes.get(reader.cursor).copied() == Some(0xc0) {
                reader.nil()?;
                None
            } else {
                Some(reader.text(256)?)
            };
            let detail = detail.unwrap_or_else(|| "reactive server error".to_owned());
            let (kind, name) = match code {
                1 => (ErrorKind::InvalidResponse, "unsupported_version"),
                2 => (ErrorKind::InvalidResponse, "malformed_frame"),
                3 => (ErrorKind::Authentication, "authentication_expired"),
                4 => (ErrorKind::Authorization, "permission_denied"),
                5 => (ErrorKind::Validation, "checkpoint_refused"),
                6 => (ErrorKind::Unavailable, "slow_consumer"),
                7 => (ErrorKind::Unavailable, "overloaded"),
                8 => (ErrorKind::Unavailable, "unavailable"),
                _ => return Err(msgpack_error("unknown reactive error code")),
            };
            Err(Error::new(kind, name, format!("{name}: {detail}")))
        }
        11 => {
            reader.array(1)?;
            let mode = reader.u32()?;
            if mode != 1 && mode != 2 {
                return Err(msgpack_error("unknown backpressure mode"));
            }
            if mode == 1 {
                Ok(serde_json::json!({
                    "reset": {
                        "reason": "backpressure",
                        "detail": "Reactive server requested a bounded delivery pause."
                    }
                }))
            } else {
                Ok(serde_json::json!({}))
            }
        }
        _ => Err(msgpack_error(
            "message is not a server-to-client reactive frame",
        )),
    }
}

impl<'a> MsgpackReader<'a> {
    fn array_count(&mut self, max: usize) -> Result<usize> {
        let tag = self.byte()?;
        let count = match tag {
            0x90..=0x9f => usize::from(tag - 0x90),
            0xdc => usize::from(u16::from_be_bytes(self.take(2)?.try_into().unwrap())),
            0xdd => usize::try_from(u32::from_be_bytes(self.take(4)?.try_into().unwrap()))
                .map_err(|_| msgpack_error("array length overflow"))?,
            _ => return Err(msgpack_error("expected indexed array")),
        };
        if count > max || count > MSGPACK_MAX_ARRAY_ITEMS {
            return Err(msgpack_error("array exceeds its bound"));
        }
        Ok(count)
    }
}

fn decode_msgpack_snapshot(reader: &mut MsgpackReader<'_>) -> Result<serde_json::Value> {
    reader.array(6)?;
    let generation = reader.u64()?;
    let authorization_generation = reader.u64()?;
    let revision = reader.u64()?;
    let count = reader.array_count(10_000)?;
    let mut items = Vec::with_capacity(count);
    for _ in 0..count {
        reader.array(2)?;
        let identity = reader.text(256)?;
        let fields = decode_msgpack_fields(reader)?;
        items.push(serde_json::json!({"identity": identity, "fields": fields}));
    }
    let digest = reader.text(64)?;
    let checkpoint = BASE64.encode(reader.binary(1, MAX_CHECKPOINT_BYTES)?);
    Ok(
        serde_json::json!({"view_generation": generation, "authorization_generation": authorization_generation, "revision": revision.to_string(), "items": items, "digest": digest, "checkpoint": checkpoint}),
    )
}

fn decode_msgpack_fields(reader: &mut MsgpackReader<'_>) -> Result<serde_json::Value> {
    let count = reader.array_count(1024)?;
    let mut fields = Vec::with_capacity(count);
    let mut aggregate_bytes = 0usize;
    for _ in 0..count {
        reader.array(2)?;
        let path = reader.text(256)?;
        if path.is_empty() {
            return Err(msgpack_error("projected field path is empty"));
        }
        let bytes = reader.binary(0, MSGPACK_MAX_BINARY_BYTES)?;
        aggregate_bytes = aggregate_bytes
            .checked_add(bytes.len())
            .ok_or_else(|| msgpack_error("projected fields exceed their aggregate bound"))?;
        if aggregate_bytes > MSGPACK_MAX_BINARY_BYTES {
            return Err(msgpack_error(
                "projected fields exceed their aggregate bound",
            ));
        }
        if fields.iter().any(|field: &serde_json::Value| {
            field.get("path").and_then(serde_json::Value::as_str) == Some(path.as_str())
        }) {
            return Err(msgpack_error("duplicate projected field"));
        }
        // Keep the producer's canonical JSON bytes opaque. Serde's array
        // representation is only a transport bridge back into ResultItem;
        // the bytes are never parsed or normalized here.
        fields.push(serde_json::json!({"path": path, "value": bytes}));
    }
    Ok(serde_json::Value::Array(fields))
}

fn decode_msgpack_operations(reader: &mut MsgpackReader<'_>) -> Result<Vec<serde_json::Value>> {
    let count = reader.array_count(1024)?;
    let mut operations = Vec::with_capacity(count);
    for _ in 0..count {
        let variant_count = reader.array_count(4)?;
        let variant = reader.u32()?;
        let identity = reader.text(256)?;
        let operation = match variant {
            1 => {
                if variant_count != 4 {
                    return Err(msgpack_error("invalid insert shape"));
                }
                let position = reader.u32()?;
                let fields = decode_msgpack_fields(reader)?;
                serde_json::json!({"kind":"insert","identity":identity,"position":position,"value":fields_to_value_map(fields)})
            }
            2 => {
                if variant_count != 3 {
                    return Err(msgpack_error("invalid update shape"));
                }
                let fields = decode_msgpack_fields(reader)?;
                serde_json::json!({"kind":"update","identity":identity,"value":fields_to_value_map(fields)})
            }
            3 => {
                if variant_count != 3 {
                    return Err(msgpack_error("invalid patch shape"));
                }
                let count = reader.array_count(1024)?;
                let mut changes = Vec::with_capacity(count);
                let mut aggregate_bytes = 0usize;
                for _ in 0..count {
                    reader.array(2)?;
                    let path = reader.text(256)?;
                    let value = if reader.bytes.get(reader.cursor).copied() == Some(0xc0) {
                        reader.nil()?;
                        serde_json::Value::Null
                    } else {
                        let bytes = reader.binary(0, MSGPACK_MAX_BINARY_BYTES)?;
                        aggregate_bytes = aggregate_bytes
                            .checked_add(bytes.len())
                            .ok_or_else(|| msgpack_error("patch exceeds its aggregate bound"))?;
                        if aggregate_bytes > MSGPACK_MAX_BINARY_BYTES {
                            return Err(msgpack_error("patch exceeds its aggregate bound"));
                        }
                        serde_json::json!(bytes)
                    };
                    if changes.iter().any(|change: &serde_json::Value| {
                        change.get("path").and_then(serde_json::Value::as_str)
                            == Some(path.as_str())
                    }) {
                        return Err(msgpack_error("duplicate patch field"));
                    }
                    changes.push(serde_json::json!({"path": path, "value": value}));
                }
                serde_json::json!({"kind":"patch","identity":identity,"changes":changes})
            }
            4 => {
                if variant_count != 2 {
                    return Err(msgpack_error("invalid remove shape"));
                }
                serde_json::json!({"kind":"remove","identity":identity})
            }
            5 => {
                if variant_count != 3 {
                    return Err(msgpack_error("invalid move shape"));
                }
                let position = reader.u32()?;
                serde_json::json!({"kind":"move","identity":identity,"position":position})
            }
            _ => return Err(msgpack_error("unknown operation variant")),
        };
        operations.push(operation);
    }
    Ok(operations)
}

/// Convert the indexed wire representation to the public map representation
/// without interpreting the projected byte strings.
fn fields_to_value_map(fields: serde_json::Value) -> serde_json::Value {
    let mut values = serde_json::Map::new();
    if let serde_json::Value::Array(fields) = fields {
        for field in fields {
            if let (Some(path), Some(value)) = (
                field.get("path").and_then(serde_json::Value::as_str),
                field.get("value"),
            ) {
                values.insert(path.to_owned(), value.clone());
            }
        }
    }
    serde_json::Value::Object(values)
}

fn decode_reset_reason(value: u32) -> Result<&'static str> {
    match value {
        1 => Ok("checkpoint_expired"),
        2 => Ok("retention_advanced"),
        3 => Ok("generation_changed"),
        4 => Ok("checkpoint_not_current"),
        5 => Ok("view_diverged"),
        6 => Ok("oversized"),
        7 => Ok("invalid_operations"),
        8 => Ok("invalid_checkpoint"),
        9 => Ok("backpressure"),
        _ => Err(msgpack_error("unknown reset reason")),
    }
}

fn append_offset(base: &str, offset: &str) -> String {
    // Keep immutable attach query fields (for example `cut`) while replacing
    // the only mutable cache-key field, `offset`.
    let (without_fragment, fragment) = base.split_once('#').map_or((base, ""), |(v, f)| (v, f));
    let (path, query) = without_fragment
        .split_once('?')
        .map_or((without_fragment, ""), |(p, q)| (p, q));
    let mut fields = query
        .split('&')
        .filter(|field| {
            let field = *field;
            !field.is_empty()
                && !field
                    .split_once('=')
                    .map_or(field, |(name, _)| name)
                    .eq_ignore_ascii_case("offset")
        })
        .map(str::to_owned)
        .collect::<Vec<_>>();
    fields.push(format!("offset={}", percent_encode(offset)));
    let mut result = format!("{path}?{}", fields.join("&"));
    if !fragment.is_empty() {
        result.push('#');
        result.push_str(fragment);
    }
    result
}

/// Resolves one delivery URL against the configured sync origin. Relative
/// canonical paths resolve against it; absolute member-issued URLs pass.
fn resolve_delivery(origin: &str, url: &str) -> String {
    if url.starts_with('/') {
        format!("{origin}{url}")
    } else {
        url.to_owned()
    }
}

fn canonical_log_path(view: &str, generation: u64) -> String {
    format!(
        "/api/v1/reactive/views/{}/{generation}/log",
        percent_encode(view)
    )
}

fn canonical_snapshot_path(view: &str, generation: u64, index: usize) -> String {
    format!(
        "/api/v1/reactive/views/{}/{generation}/snapshot/{index}",
        percent_encode(view)
    )
}

/// Accept only member-issued sync URLs. Relative canonical paths are kept
/// relative and resolved against the configured sync origin at delivery;
/// query parameters are intentionally retained because snapshot cuts are
/// part of immutable URL identity.
fn advertised_url(advertised: &str, expected_path: &str) -> std::result::Result<String, String> {
    let advertised = advertised.trim();
    let url = if advertised.starts_with('/') {
        if advertised.starts_with("//") {
            return Err("reactive URL must not contain an alternate origin".to_owned());
        }
        advertised.to_owned()
    } else {
        let (scheme, remainder) = advertised
            .split_once("://")
            .ok_or_else(|| "reactive URL must be absolute HTTPS or a canonical path".to_owned())?;
        if !scheme.eq_ignore_ascii_case("https") {
            return Err("reactive URL must use HTTPS".to_owned());
        }
        let authority_end = remainder.find(['/', '?', '#']).unwrap_or(remainder.len());
        let authority = &remainder[..authority_end];
        if authority != "sync.api.terrascale.tech" {
            return Err("reactive URL origin is not an allowed sync origin".to_owned());
        }
        advertised.to_owned()
    };
    let path = if url.starts_with('/') {
        url.as_str()
    } else {
        url.split_once("//")
            .and_then(|(_, remainder)| remainder.find('/').map(|index| &remainder[index..]))
            .unwrap_or("/")
    };
    let path = path.split(['?', '#']).next().unwrap_or(path);
    if path != expected_path {
        return Err("reactive URL path does not match its attached view".to_owned());
    }
    Ok(url)
}

fn percent_encode(value: &str) -> String {
    let mut encoded = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            encoded.push(char::from(byte));
        } else {
            encoded.push('%');
            encoded.push(char::from(b"0123456789ABCDEF"[(byte >> 4) as usize]));
            encoded.push(char::from(b"0123456789ABCDEF"[(byte & 0x0f) as usize]));
        }
    }
    encoded
}

fn is_retryable(error: &Error) -> bool {
    if matches!(
        error.code.as_str(),
        "slow_consumer"
            | "auth_expired"
            | "authentication_expired"
            | "permission_changed"
            | "permission_denied"
            | "checkpoint_refused"
            | "overloaded"
            | "unavailable"
            | "malformed_frame"
            | "unsupported_version"
    ) {
        return false;
    }
    matches!(
        error.kind,
        ErrorKind::Transport | ErrorKind::Unavailable | ErrorKind::Server
    ) || error
        .status
        .is_some_and(|status| matches!(status, 408 | 425))
}

/// `Shared::send_typed` annotates all decode failures with its route code.
/// Recover the protocol's typed MessagePack vocabulary at the reactive
/// boundary so callers can distinguish malformed/unsupported frames from a
/// typed remote authentication or availability failure.
fn normalize_msgpack_error(mut error: Error) -> Error {
    if error.code != "reactive_poll.invalid_response" && error.code != "invalid_response" {
        return error;
    }
    let message = error.message.to_ascii_lowercase();
    let (kind, code) = if message.contains("unsupported reactive messagepack protocol version")
        || message.contains("unsupported_version")
    {
        (ErrorKind::InvalidResponse, "unsupported_version")
    } else if message.contains("authentication_expired") {
        (ErrorKind::Authentication, "authentication_expired")
    } else if message.contains("permission_denied") {
        (ErrorKind::Authorization, "permission_denied")
    } else if message.contains("checkpoint_refused") {
        (ErrorKind::Validation, "checkpoint_refused")
    } else if message.contains("slow_consumer") {
        (ErrorKind::Unavailable, "slow_consumer")
    } else if message.contains("overloaded") {
        (ErrorKind::Unavailable, "overloaded")
    } else if message.contains("unavailable") {
        (ErrorKind::Unavailable, "unavailable")
    } else if message.contains("malformed reactive messagepack frame")
        || message.contains("malformed_frame")
    {
        (ErrorKind::InvalidResponse, "malformed_frame")
    } else {
        return error;
    };
    error.kind = kind;
    error.code = code.to_owned();
    error
}

/// HTTP status and server error codes are the JSON counterpart of typed
/// reset frames. In particular, 410 means the retained immutable log has
/// expired and must never be retried as a transient availability failure.
fn http_reset_reason(error: &Error) -> Option<ResetReason> {
    let reason = match error.code.trim().to_ascii_lowercase().as_str() {
        "permission_changed" | "permission_denied" | "auth_changed" => {
            ResetReason::PermissionChanged
        }
        "auth_expired" | "authentication_expired" => ResetReason::AuthExpired,
        "region_moved" | "misdirected_region" => ResetReason::RegionMoved,
        "schema_changed" => ResetReason::SchemaChanged,
        "generation_changed" => ResetReason::GenerationChanged,
        "history_expired" | "retention" | "retention_advanced" => ResetReason::HistoryExpired,
        "slow_consumer" | "backpressure" => ResetReason::Backpressure,
        "checkpoint_expired" => ResetReason::CheckpointExpired,
        "checkpoint_not_current" | "checkpoint_refused" => ResetReason::CheckpointNotCurrent,
        "view_diverged" => ResetReason::ViewDiverged,
        "invalid_checkpoint" => ResetReason::InvalidCheckpoint,
        "oversized" => ResetReason::Oversized,
        "invalid_operations" => ResetReason::InvalidOperations,
        _ => match error.status {
            Some(410) => ResetReason::HistoryExpired,
            Some(409) => ResetReason::CheckpointNotCurrent,
            Some(404) => ResetReason::GenerationChanged,
            Some(401) => ResetReason::AuthExpired,
            Some(403) => ResetReason::PermissionChanged,
            Some(429) => ResetReason::Backpressure,
            _ => return None,
        },
    };
    Some(reason)
}

pub(crate) fn cancelled_error() -> Error {
    Error::new(
        ErrorKind::Transport,
        "cancelled",
        "The reactive poll was cancelled.",
    )
}

#[cfg(test)]
mod cancellation_tests {
    use super::CancellationToken;
    use futures_util::task::{waker, ArcWake};
    use std::future::Future;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;
    use std::task::{Context, Poll};

    #[derive(Default)]
    struct WakeCount(AtomicUsize);
    impl ArcWake for WakeCount {
        fn wake_by_ref(this: &Arc<Self>) {
            this.0.fetch_add(1, Ordering::SeqCst);
        }
    }

    #[test]
    fn cancellation_sleeps_and_wakes_every_clone_without_lost_notification() {
        let token = CancellationToken::new();
        let clone = token.clone();
        let wakes = Arc::new(WakeCount::default());
        let waker = waker(wakes.clone());
        let mut context = Context::from_waker(&waker);
        let mut first = Box::pin(token.cancelled());
        let mut second = Box::pin(clone.cancelled());
        assert_eq!(first.as_mut().poll(&mut context), Poll::Pending);
        assert_eq!(second.as_mut().poll(&mut context), Poll::Pending);
        assert_eq!(wakes.0.load(Ordering::SeqCst), 0);
        token.cancel();
        assert_eq!(wakes.0.load(Ordering::SeqCst), 2);
        assert_eq!(first.as_mut().poll(&mut context), Poll::Ready(()));
        assert_eq!(second.as_mut().poll(&mut context), Poll::Ready(()));
        assert_eq!(
            Box::pin(token.cancelled()).as_mut().poll(&mut context),
            Poll::Ready(())
        );
    }
}
