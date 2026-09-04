//! Reactive live queries: `subscribe` / `execute`, checkpoints, typed resets.
//!
//! `.subscribe()` uses bounded HTTP long-polling on `sync.api.` by default.
//! [`Transport::WebSocket`] remains an explicit boundary and fails closed in
//! this HTTP-only crate until a WebSocket transport is published. `.execute()`
//! is "subscribe once". A checkpoint is an opaque authorized-view position;
//! any mismatch returns a typed [`ResetReason`] with a fresh authorized
//! snapshot (GOAL D-46).

use std::collections::VecDeque;
use std::fmt;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use base64::engine::{general_purpose::STANDARD as BASE64, Engine as _};
use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
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
        Ok(Self::new(match wire {
            CheckpointWire::Bytes(bytes) => bytes,
            CheckpointWire::Text(text) => {
                BASE64.decode(&text).unwrap_or_else(|_| text.into_bytes())
            }
        }))
    }
}

impl Checkpoint {
    /// Wrap server-issued checkpoint bytes.
    #[must_use]
    pub fn new(bytes: Vec<u8>) -> Self {
        Self { bytes }
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
        if bytes.is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_checkpoint",
                "Checkpoint cannot be empty. Resume with a fresh snapshot.",
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
}

/// A bounded retry budget for CDN log reads. A poll is one request; retries
/// only cover transport and server availability failures.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PollOptions {
    /// Maximum number of requests, including the first attempt.
    pub max_attempts: usize,
}

impl PollOptions {
    /// Construct a budget. Zero is normalized to one request.
    #[must_use]
    pub fn new(max_attempts: usize) -> Self {
        Self {
            max_attempts: max_attempts.max(1),
        }
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
        self.cancelled.store(true, Ordering::Release);
    }

    /// Whether cancellation was requested.
    #[must_use]
    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::Acquire)
    }

    async fn cancelled(&self) {
        while !self.is_cancelled() {
            tokio::task::yield_now().await;
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
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

/// One immutable CDN log response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
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

/// Result of consuming one immutable log response.
#[derive(Debug, Clone, PartialEq)]
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
    /// Projected value.
    pub value: serde_json::Value,
}

/// Authorized snapshot bootstrapping or replacing a view.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ViewSnapshot {
    /// View generation; deltas from another generation force a reset.
    #[serde(alias = "viewGeneration")]
    pub view_generation: u64,
    /// Revision of this snapshot (the journal cut).
    #[serde(alias = "baseRevision")]
    pub revision: String,
    /// Full ordered result.
    pub items: Vec<ResultItem>,
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
        value: serde_json::Value,
    },
    Update {
        identity: String,
        value: serde_json::Value,
    },
    Patch {
        identity: String,
        changes: serde_json::Value,
    },
    Remove {
        identity: String,
    },
    Move {
        identity: String,
        position: u64,
    },
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
    revision: Option<String>,
    items: Vec<ResultItem>,
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
            revision: None,
            items: Vec::new(),
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

    fn install_checkpoint(&mut self, checkpoint: Checkpoint) {
        self.checkpoint = Some(checkpoint);
    }

    /// Install an authorized snapshot (bootstrap or reset).
    pub fn apply_snapshot(&mut self, snapshot: ViewSnapshot) {
        self.generation = Some(snapshot.view_generation);
        self.revision = Some(snapshot.revision);
        self.items = snapshot.items;
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
        match (&self.generation, &self.revision) {
            (Some(generation), _) if *generation != delta.view_generation => {
                ApplyOutcome::ResetRequired(ResetReason::GenerationChanged)
            }
            (Some(generation), Some(revision))
                if *generation == delta.view_generation && *revision == delta.from_revision =>
            {
                for operation in &delta.operations {
                    apply_operation(&mut self.items, operation);
                }
                self.revision = Some(delta.to_revision.clone());
                self.checkpoint = Some(delta.checkpoint.clone());
                ApplyOutcome::Applied
            }
            (Some(_), Some(revision)) if *revision == delta.to_revision => ApplyOutcome::Duplicate,
            (Some(_), _) => ApplyOutcome::ResetRequired(ResetReason::Reconnect),
            (None, _) => ApplyOutcome::ResetRequired(ResetReason::Reconnect),
        }
    }
}

fn apply_operation(items: &mut Vec<ResultItem>, operation: &ViewOperation) {
    match operation {
        ViewOperation::Insert {
            identity,
            position,
            value,
        } => {
            let index = (*position as usize).min(items.len());
            items.insert(
                index,
                ResultItem {
                    identity: identity.clone(),
                    value: value.clone(),
                },
            );
        }
        ViewOperation::Update { identity, value } => {
            if let Some(item) = items.iter_mut().find(|item| &item.identity == identity) {
                item.value = value.clone();
            }
        }
        ViewOperation::Patch { identity, changes } => {
            if let Some(item) = items.iter_mut().find(|item| &item.identity == identity) {
                merge_patch(&mut item.value, changes);
            }
        }
        ViewOperation::Remove { identity } => {
            items.retain(|item| &item.identity != identity);
        }
        ViewOperation::Move { identity, position } => {
            if let Some(index) = items.iter().position(|item| &item.identity == identity) {
                let item = items.remove(index);
                let target = (*position as usize).min(items.len());
                items.insert(target, item);
            }
        }
    }
}

fn merge_patch(value: &mut serde_json::Value, changes: &serde_json::Value) {
    match (value, changes) {
        (serde_json::Value::Object(target), serde_json::Value::Object(changes)) => {
            for (key, change) in changes {
                if change.is_null() {
                    target.remove(key);
                } else {
                    merge_patch(
                        target.entry(key.clone()).or_insert(serde_json::Value::Null),
                        change,
                    );
                }
            }
        }
        (target, change) => *target = change.clone(),
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
        if options.transport == Transport::WebSocket {
            return Err(Error::new(
                ErrorKind::Unavailable,
                "websocket_not_supported",
                "WebSocket delivery is an explicit opt-in boundary; this HTTP transport does not open WebSocket connections.",
            ));
        }
        let url = format!(
            "{}/reactive/views",
            self.shared.database_root_in(scope, options.region_hint)?
        );
        let body = serde_json::json!({
            "statement": query.sql(),
            "parameters": query.params(),
            "transport": options.transport,
            "checkpoint": options.checkpoint.as_ref().map(Checkpoint::to_base64),
        });
        let mut request = Request::json("reactive.subscribe", Method::Post, url, &body)?;
        if let Some(hint) = options.region_hint {
            request.headers.push((
                "x-terrascale-region-hint".to_owned(),
                hint.as_str().to_owned(),
            ));
        }
        self.shared.send_typed(request).await
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
        let log_url = view.log_url.trim();
        if log_url.is_empty() {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "missing_log_url",
                "The reactive attach response did not include its immutable log URL.",
            ));
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
        let url = append_offset(log_url, offset);
        let mut last_error = None;
        for attempt in 0..attempts {
            if cancellation.is_cancelled() {
                return Err(cancelled_error());
            }
            let mut request = Request {
                route_name: "reactive.poll".to_owned(),
                method: Method::Get,
                url: url.clone(),
                headers: Vec::new(),
                body: None,
            };
            request.headers.push((
                VIEW_CAPABILITY_HEADER.to_owned(),
                view.capability.clone().expect("capability checked above"),
            ));
            request
                .headers
                .push(("accept".to_owned(), "application/json".to_owned()));
            let result = tokio::select! {
                _ = cancellation.cancelled() => return Err(cancelled_error()),
                result = self.shared.send_typed::<PollResponse>(request) => result,
            };
            match result {
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
        let view = self.subscribe(scope, query, options).await?;
        if view.log_url.trim().is_empty() {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "missing_log_url",
                "The reactive attach response did not include its immutable log URL.",
            ));
        }
        let mut state = Subscription::new(view.view_id.clone());
        state.apply_snapshot(view.snapshot.clone());
        Ok(LiveSubscription {
            client: self.clone(),
            view,
            state,
            options: PollOptions::default(),
            cancellation: CancellationToken::new(),
        })
    }

    /// Execute a live query once and return its rows ("subscribe once").
    pub async fn execute(&self, scope: &DatabaseScope, query: &Query) -> Result<SqlRows> {
        query.validate()?;
        let url = format!("{}/sql", self.shared.database_root(scope)?);
        let body = serde_json::json!({
            "statement": query.sql(),
            "parameters": query.params(),
        });
        let request = Request::json("reactive.execute", Method::Post, url, &body)?;
        self.shared.send_typed(request).await
    }
}

/// View attachment returned by [`ReactiveClient::subscribe`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
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
            return Err(cancelled_error());
        }
        let offset = self
            .state
            .revision()
            .unwrap_or(self.view.snapshot.revision.as_str());
        let response = match self
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
                    detail: Some(error.message),
                }));
            }
            Err(error) => return Err(error),
        };
        if let Some(mut reset) = response.reset {
            if reset.checkpoint.is_none() {
                reset.checkpoint = self.state.checkpoint().cloned();
            }
            if let Some(snapshot) = reset.snapshot.clone() {
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
pub const VIEW_CAPABILITY_HEADER: &str = "x-terrascale-view-capability";

fn append_offset(base: &str, offset: &str) -> String {
    let separator = if base.contains('?') { '&' } else { '?' };
    format!("{base}{separator}offset={}", percent_encode(offset))
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
    matches!(
        error.kind,
        ErrorKind::Transport | ErrorKind::Unavailable | ErrorKind::Server
    ) || error
        .status
        .is_some_and(|status| matches!(status, 408 | 425 | 429))
}

fn cancelled_error() -> Error {
    Error::new(
        ErrorKind::Transport,
        "cancelled",
        "The reactive poll was cancelled.",
    )
}
