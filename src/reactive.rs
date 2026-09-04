//! Reactive live queries: `subscribe` / `execute`, checkpoints, typed resets.
//!
//! `.subscribe()` uses bounded HTTP long-polling on `sync.api.` by default;
//! `.subscribe()` with [`Transport::WebSocket`] opts into the direct
//! transport. `.execute()` is "subscribe once". A checkpoint is an opaque
//! authorized-view position; any mismatch returns a typed [`ResetReason`]
//! with a fresh authorized snapshot (GOAL D-46).

use std::collections::VecDeque;
use std::fmt;

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
    LongPolling,
    /// Direct opt-in transport; cannot be cached or collapsed.
    #[serde(rename = "websocket")]
    WebSocket,
}

/// Opaque authorized-view position. Never constructed by hand: it binds view
/// identity and generation, database/branch incarnation, schema version,
/// authorization revision, entitlement epoch, region/replica epoch, the
/// retained journal boundary, and expiry.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Checkpoint {
    bytes: Vec<u8>,
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

    /// Base64 form for storage (local stores, URLs, logs).
    #[must_use]
    pub fn to_base64(&self) -> String {
        BASE64.encode(&self.bytes)
    }

    /// Restore from [`Checkpoint::to_base64`].
    pub fn from_base64(encoded: &str) -> Result<Self> {
        BASE64.decode(encoded.trim()).map(Self::new).map_err(|e| {
            Error::new(
                ErrorKind::Validation,
                "invalid_checkpoint",
                format!("Checkpoint is not valid base64: {e}. Resume with a fresh snapshot."),
            )
        })
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
    pub view_generation: u64,
    /// Revision of this snapshot (the journal cut).
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
    pub view_generation: u64,
    /// Revision the delta starts from.
    pub from_revision: String,
    /// Revision the delta ends at.
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
#[derive(Debug)]
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

    /// Execute a live query once and return its rows ("subscribe once").
    pub async fn execute(&self, scope: &DatabaseScope, query: &Query) -> Result<SqlRows> {
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
    pub view_id: String,
    /// View generation.
    pub generation: u64,
    /// Opening snapshot.
    pub snapshot: ViewSnapshot,
    /// Resume checkpoint (also inside the snapshot).
    pub checkpoint: Checkpoint,
    /// Capability header value for `sync.api.` reads (never in URLs).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub capability: Option<String>,
}
