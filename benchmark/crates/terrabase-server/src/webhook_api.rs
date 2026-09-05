//! Durable change-stream webhooks (S-07, D-39, D-64).
//!
//! Production composition supplies all five ports. The Engine and shared
//! member guard have adapters here; secret management and HTTPS/TLS remain
//! explicit host-owned providers. A missing port is a hard error: this surface
//! never quietly substitutes an in-memory queue, an in-process secret, or a
//! direct URL client.

use std::collections::BTreeMap;
use std::fmt;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::Router;
use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::header::{CONTENT_TYPE, HeaderMap, HeaderValue};
use axum::http::{Method, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post};
use serde::{Deserialize, Serialize};

use terrabase::engine::Engine;
use terrabase::types::{CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion};
use terrabase::{CommitError, CommitOutcome, EngineError, LocalDurability, OperationId, TenantId};

use crate::guards::AbuseGuards;

/// Maximum endpoint URL length accepted by the service.
pub const MAX_ENDPOINT_BYTES: usize = 2_048;
/// Maximum webhook request body accepted by the HTTP router.
pub const MAX_WEBHOOK_BODY_BYTES: usize = 64 * 1024;
/// Maximum durable publication body sent to a webhook.
pub const MAX_DELIVERY_BYTES: usize = 1_048_576;
/// Maximum publications read in one journal page.
pub const MAX_JOURNAL_PAGE: usize = 256;
/// Maximum attempts, including the first delivery attempt.
pub const MAX_ATTEMPTS: u32 = 8;
/// The retry horizon.  A publication never remains retryable forever.
pub const RETRY_HORIZON_SECONDS: u64 = 24 * 60 * 60;
/// The largest retry delay.
pub const MAX_RETRY_DELAY_SECONDS: u64 = 60 * 60;

const WEBHOOK_ENDPOINTS_COLLECTION: &str = "__terrascale_webhook_endpoints";
const WEBHOOK_DELIVERIES_COLLECTION: &str = "__terrascale_webhook_deliveries";

/// The authenticated tenant/project/database scope of one API operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantScope {
    /// Tenant identity from the authenticated control-plane principal.
    pub tenant: String,
    /// Project identity from the authenticated control-plane principal.
    pub project: String,
    /// Database named by the route.
    pub database: String,
}

impl TenantScope {
    /// Creates a scope, rejecting empty identities.
    ///
    /// # Errors
    ///
    /// Returns an invalid-request error when any scope identity is empty.
    pub fn new(
        tenant: impl Into<String>,
        project: impl Into<String>,
        database: impl Into<String>,
    ) -> Result<Self, WebhookError> {
        let scope =
            Self { tenant: tenant.into(), project: project.into(), database: database.into() };
        if scope.tenant.is_empty() || scope.project.is_empty() || scope.database.is_empty() {
            return Err(WebhookError::invalid_request("tenant scope is incomplete"));
        }
        Ok(scope)
    }
}

/// A webhook endpoint as stored durably.  Secrets are never part of this
/// value, serialized responses, delivery logs, or error messages.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct WebhookEndpoint {
    /// Stable durable endpoint identity.
    pub id: String,
    /// Canonical HTTPS destination.
    pub endpoint: String,
    /// Creation time in unix seconds.
    pub created_at: u64,
    /// Whether new deliveries are disabled by deletion.
    pub deleted: bool,
}

/// Input for endpoint creation.
#[derive(Clone, Debug, Deserialize)]
pub struct CreateWebhook {
    /// HTTPS destination.  `url` is accepted for old clients.
    #[serde(alias = "url")]
    pub endpoint: String,
}

/// One durable publication from the shared revision/journal stream.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Publication {
    /// Stable journal sequence, used as the cursor and part of idempotency.
    pub revision: u64,
    /// Stable operation identity from the publication journal.
    pub event_id: String,
    /// Canonical revision bytes, already encoded by the shared view engine.
    pub body: Vec<u8>,
}

/// Durable delivery state and the latest redacted outcome.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct DeliveryRecord {
    /// Stable idempotency identity (`endpoint/event`).
    pub id: String,
    /// Publication event identity.
    pub event_id: String,
    /// Journal revision.
    pub revision: u64,
    /// Start of the current automatic retry window in Unix seconds.
    pub retry_started_at: u64,
    /// Number of attempts made.
    pub attempts: u32,
    /// Next eligible attempt, or zero when terminal/succeeded.
    pub next_attempt_at: u64,
    /// Last HTTP status, if the transport received one.
    pub status: Option<u16>,
    /// `true` after a 2xx response.
    pub delivered: bool,
    /// `true` when no further retries will be attempted.
    pub terminal: bool,
}

/// A durable delivery log page.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct DeliveryLog {
    /// Entries in durable attempt order.
    pub deliveries: Vec<DeliveryRecord>,
}

/// A redacted durable-store failure.  Implementations may retain detailed
/// diagnostics in private structured logs, but must not return them here.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StorageError {
    /// The durable store is unavailable or rejected an operation.
    Unavailable,
    /// The named record does not exist.
    NotFound,
    /// An idempotency conflict was detected.
    Conflict,
}

/// A redacted secret-provider failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SecretError {
    /// Secret provider is unavailable or denied placement.
    Unavailable,
    /// No secret exists for the endpoint.
    NotFound,
}

/// A redacted network delivery failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DeliveryError {
    /// The target failed the transport's DNS/network egress policy.
    TargetRejected,
    /// The request could not be completed; it may be retried.
    Retryable,
    /// The transport refused the request permanently.
    Permanent,
}

/// Durable result of one webhook delivery attempt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DeliveryAttempt {
    /// Number of attempts including this one.
    pub attempts: u32,
    /// Earliest next attempt in Unix seconds, or zero when terminal.
    pub next_attempt_at: u64,
    /// HTTP status when the transport received a response.
    pub status: Option<u16>,
    /// Whether the endpoint acknowledged delivery.
    pub delivered: bool,
    /// Whether automatic delivery attempts have ended.
    pub terminal: bool,
}

/// Durable endpoint and delivery state port.  This must be backed by `SQLite`
/// in production; the service does not provide a memory implementation.
pub trait WebhookStorage: Send + Sync + 'static {
    /// Creates one endpoint durably and atomically with its stable id.
    ///
    /// # Errors
    ///
    /// Returns a scope, storage, or concurrent-write failure.
    fn create_endpoint(
        &self,
        scope: &TenantScope,
        endpoint: &str,
        now: u64,
    ) -> Result<WebhookEndpoint, StorageError>;
    /// Removes an endpoint during create rollback or explicit deletion.
    ///
    /// # Errors
    ///
    /// Returns a missing endpoint, scope, storage, or concurrent-write failure.
    fn delete_endpoint(
        &self,
        scope: &TenantScope,
        id: &str,
    ) -> Result<WebhookEndpoint, StorageError>;
    /// Finds one non-secret endpoint.
    ///
    /// # Errors
    ///
    /// Returns scope, storage, or decoding failures; absence is `None`.
    fn endpoint(
        &self,
        scope: &TenantScope,
        id: &str,
    ) -> Result<Option<WebhookEndpoint>, StorageError>;
    /// Lists endpoints, including deleted records when the caller asks for them.
    ///
    /// # Errors
    ///
    /// Returns scope, storage, or decoding failures.
    fn list_endpoints(
        &self,
        scope: &TenantScope,
        include_deleted: bool,
    ) -> Result<Vec<WebhookEndpoint>, StorageError>;
    /// Ensures one durable delivery row exists.  Repeated calls return the
    /// same row and therefore provide idempotency after process crashes.
    ///
    /// # Errors
    ///
    /// Returns a missing endpoint, conflicting publication identity, scope, or storage failure.
    fn ensure_delivery(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        publication: &Publication,
        now: u64,
    ) -> Result<DeliveryRecord, StorageError>;
    /// Persists the result of one attempt before any subsequent attempt.
    ///
    /// # Errors
    ///
    /// Returns a missing delivery, scope, storage, or concurrent-write failure.
    fn record_attempt(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        expected: &DeliveryRecord,
        attempt: &DeliveryAttempt,
    ) -> Result<(), StorageError>;
    /// Terminates an expired retry window without counting a network attempt.
    /// The expected record prevents expiration from overwriting a concurrent replay.
    ///
    /// # Errors
    ///
    /// Returns a missing delivery, premature expiry, concurrent change, or storage failure.
    fn expire_delivery(
        &self,
        scope: &TenantScope,
        expected: &DeliveryRecord,
        now: u64,
    ) -> Result<(), StorageError>;
    /// Reads the durable delivery log.
    ///
    /// # Errors
    ///
    /// Returns a missing endpoint, scope, storage, or decoding failure.
    fn delivery_log(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        limit: usize,
    ) -> Result<DeliveryLog, StorageError>;
    /// Returns at most `limit` nonterminal deliveries due by `now`, oldest first.
    /// Selection is independent of the publication journal cursor.
    ///
    /// # Errors
    ///
    /// Returns scope, storage, or decoding failures.
    fn due_deliveries(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        now: u64,
        limit: usize,
    ) -> Result<Vec<DeliveryRecord>, StorageError>;
    /// Marks one delivery eligible immediately for a replay.
    ///
    /// # Errors
    ///
    /// Returns a missing endpoint/delivery, scope, storage, or concurrent-write failure.
    fn replay_delivery(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
        now: u64,
    ) -> Result<DeliveryRecord, StorageError>;
}

/// Durable shared publication journal port.  The page must be derived from
/// the same revision line used by HTTP views and `WebSockets`.
pub trait WebhookJournal: Send + Sync + 'static {
    /// Reads publications strictly after `cursor` for one database.
    ///
    /// # Errors
    ///
    /// Returns scope, storage, or publication-decoding failures.
    fn page(
        &self,
        scope: &TenantScope,
        cursor: u64,
        limit: usize,
    ) -> Result<Vec<Publication>, StorageError>;
}

/// Engine-backed publication journal adapter.
///
/// `Engine::journal_rows` is the same durable revision line consumed by the
/// reactive and replication paths. The canonical row bytes are forwarded
/// unchanged; an event identity is a BLAKE3 digest of those bytes, which is
/// stable across restart and cannot collide merely because two documents share
/// one commit sequence.
pub struct EngineWebhookJournal {
    engine: Arc<Engine>,
}

impl EngineWebhookJournal {
    /// Binds the adapter to an already opened, durable Engine.
    #[must_use]
    pub fn new(engine: Arc<Engine>) -> Self {
        Self { engine }
    }
}

impl WebhookJournal for EngineWebhookJournal {
    fn page(
        &self,
        scope: &TenantScope,
        cursor: u64,
        limit: usize,
    ) -> Result<Vec<Publication>, StorageError> {
        let engine_scope = webhook_engine_scope(&self.engine, scope)?;
        if limit == 0 {
            return Ok(Vec::new());
        }
        let limit =
            u32::try_from(limit.min(MAX_JOURNAL_PAGE)).map_err(|_| StorageError::Unavailable)?;
        let rows = self
            .engine
            .journal_rows(&engine_scope, cursor, limit)
            .map_err(|_| StorageError::Unavailable)?;
        let mut publications = Vec::with_capacity(rows.len());
        for row in rows {
            if row.commit_sequence == 0 || row.canonical.is_empty() {
                return Err(StorageError::Unavailable);
            }
            if is_webhook_metadata_row(&row.canonical) {
                continue;
            }
            let event_id = blake3::hash(&row.canonical).to_hex().to_string();
            publications.push(Publication {
                revision: row.commit_sequence,
                event_id,
                body: row.canonical,
            });
        }
        Ok(publications)
    }
}

/// Engine-backed durable endpoint and delivery records.
///
/// The adapter uses the Engine's ordinary `SQLite` document transaction, so
/// endpoint creation, delivery idempotency, retry state, and replay survive a
/// process restart. A process-local mutex only serializes this adapter's
/// compare-and-write operations; it is not a source of state or a fallback.
pub struct EngineWebhookStorage {
    engine: Arc<Engine>,
    writes: Mutex<()>,
}

impl EngineWebhookStorage {
    /// Binds storage to an already opened, durable Engine.
    #[must_use]
    pub fn new(engine: Arc<Engine>) -> Self {
        Self { engine, writes: Mutex::new(()) }
    }

    fn engine_scope(&self, scope: &TenantScope) -> Result<terrabase::TenantScope, StorageError> {
        webhook_engine_scope(&self.engine, scope)
    }

    fn read_document(
        &self,
        scope: &TenantScope,
        collection: &str,
        id: &str,
    ) -> Result<Option<Document>, StorageError> {
        let engine_scope = self.engine_scope(scope)?;
        let snapshot =
            self.engine.read_snapshot(&engine_scope).map_err(|error| map_engine_storage(&error))?;
        let transaction = self
            .engine
            .begin(&engine_scope, snapshot, operation_id("webhook-read"))
            .map_err(|error| map_engine_storage(&error))?;
        let collection = CollectionName::try_new(collection).map_err(|_| StorageError::Conflict)?;
        let id = DocumentId::try_new(id).map_err(|_| StorageError::Conflict)?;
        transaction.read_in_collection(&collection, &id).map_err(|error| map_engine_storage(&error))
    }

    fn documents(
        &self,
        scope: &TenantScope,
        collection: &str,
    ) -> Result<Vec<Document>, StorageError> {
        let engine_scope = self.engine_scope(scope)?;
        let snapshot =
            self.engine.read_snapshot(&engine_scope).map_err(|error| map_engine_storage(&error))?;
        let collection = CollectionName::try_new(collection).map_err(|_| StorageError::Conflict)?;
        self.engine
            .snapshot_documents_in_collection(&engine_scope, &snapshot, &collection)
            .map_err(|error| map_engine_storage(&error))
    }

    fn write_document(
        &self,
        scope: &TenantScope,
        collection: &str,
        id: &str,
        value: DocumentValue,
        expected: ExpectedVersion,
    ) -> Result<(), StorageError> {
        let engine_scope = self.engine_scope(scope)?;
        let snapshot =
            self.engine.read_snapshot(&engine_scope).map_err(|error| map_engine_storage(&error))?;
        let mut transaction = self
            .engine
            .begin(&engine_scope, snapshot, operation_id("webhook-write"))
            .map_err(|error| map_engine_storage(&error))?;
        let collection = CollectionName::try_new(collection).map_err(|_| StorageError::Conflict)?;
        let id = DocumentId::try_new(id).map_err(|_| StorageError::Conflict)?;
        transaction
            .put(Document::new(id, collection, value), expected)
            .map_err(|error| map_engine_storage(&error))?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(CommitError::Conflict | CommitError::OperationIdReuse) => {
                Err(StorageError::Conflict)
            }
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(StorageError::Unavailable)
            }
        }
    }
}

fn webhook_engine_scope(
    engine: &Engine,
    scope: &TenantScope,
) -> Result<terrabase::TenantScope, StorageError> {
    let owner = engine.database_owner();
    if scope.tenant != owner.tenant().to_text()
        || scope.project != owner.project().to_text()
        || scope.database != owner.database().to_text()
    {
        return Err(StorageError::Conflict);
    }
    let tenant = TenantId::try_new(&scope.tenant).map_err(|_| StorageError::Conflict)?;
    Ok(engine.scope_for(&tenant))
}

fn map_engine_storage(error: &EngineError) -> StorageError {
    match error {
        EngineError::ScopeMismatch
        | EngineError::SnapshotEngineMismatch
        | EngineError::SnapshotTenantMismatch => StorageError::Conflict,
        _ => StorageError::Unavailable,
    }
}

fn operation_id(prefix: &str) -> OperationId {
    let id = format!("{prefix}-{}", terrabase::identity::process_generator().generate().to_text());
    OperationId::try_new(&id).expect("generated webhook operation identity is valid")
}

fn delivery_id(endpoint_id: &str, event_id: &str) -> String {
    format!("{endpoint_id}:{event_id}")
}

// SQLite document integers are signed. Keep compact legacy integers where
// possible and use exact decimal text for the upper half of the u64 range.
fn unsigned_value(value: u64) -> DocumentValue {
    i64::try_from(value)
        .map_or_else(|_| DocumentValue::String(value.to_string()), DocumentValue::Integer)
}

fn unsigned_from_value(value: &DocumentValue) -> Option<u64> {
    match value {
        DocumentValue::Integer(value) => u64::try_from(*value).ok(),
        DocumentValue::String(text) => {
            let value = text.parse::<u64>().ok()?;
            (text == &value.to_string()).then_some(value)
        }
        _ => None,
    }
}

fn endpoint_value(record: &WebhookEndpoint) -> DocumentValue {
    DocumentValue::Object(BTreeMap::from([
        ("endpoint".into(), DocumentValue::String(record.endpoint.clone())),
        ("created_at".into(), unsigned_value(record.created_at)),
        ("deleted".into(), DocumentValue::Boolean(record.deleted)),
    ]))
}

fn endpoint_from_document(document: &Document) -> Option<WebhookEndpoint> {
    let DocumentValue::Object(fields) = document.value() else { return None };
    let endpoint = match fields.get("endpoint")? {
        DocumentValue::String(value) => value.clone(),
        _ => return None,
    };
    let created_at = unsigned_from_value(fields.get("created_at")?)?;
    let deleted = match fields.get("deleted")? {
        DocumentValue::Boolean(value) => *value,
        _ => return None,
    };
    Some(WebhookEndpoint { id: document.id().as_str().to_owned(), endpoint, created_at, deleted })
}

fn delivery_value(record: &DeliveryRecord) -> DocumentValue {
    DocumentValue::Object(BTreeMap::from([
        ("event_id".into(), DocumentValue::String(record.event_id.clone())),
        ("revision".into(), unsigned_value(record.revision)),
        ("attempts".into(), DocumentValue::Integer(i64::from(record.attempts))),
        ("next_attempt_at".into(), unsigned_value(record.next_attempt_at)),
        ("retry_started_at".into(), unsigned_value(record.retry_started_at)),
        (
            "status".into(),
            record
                .status
                .map_or(DocumentValue::Null, |value| DocumentValue::Integer(i64::from(value))),
        ),
        ("delivered".into(), DocumentValue::Boolean(record.delivered)),
        ("terminal".into(), DocumentValue::Boolean(record.terminal)),
    ]))
}

fn delivery_from_document(document: &Document) -> Option<DeliveryRecord> {
    let DocumentValue::Object(fields) = document.value() else { return None };
    let string = |name: &str| match fields.get(name)? {
        DocumentValue::String(value) => Some(value.clone()),
        _ => None,
    };
    let integer = |name: &str| unsigned_from_value(fields.get(name)?);
    let boolean = |name: &str| match fields.get(name)? {
        DocumentValue::Boolean(value) => Some(*value),
        _ => None,
    };
    let status = match fields.get("status")? {
        DocumentValue::Null => None,
        DocumentValue::Integer(value) => Some(u16::try_from(*value).ok()?),
        _ => return None,
    };
    Some(DeliveryRecord {
        id: document.id().as_str().to_owned(),
        event_id: string("event_id")?,
        revision: integer("revision")?,
        attempts: u32::try_from(integer("attempts")?).ok()?,
        next_attempt_at: integer("next_attempt_at")?,
        retry_started_at: integer("retry_started_at")?,
        status,
        delivered: boolean("delivered")?,
        terminal: boolean("terminal")?,
    })
}

/// Returns whether a canonical Engine journal row belongs to webhook metadata.
/// Canonical rows start with a u32-length-prefixed collection name.
fn is_webhook_metadata_row(canonical: &[u8]) -> bool {
    let Some(length) = canonical.get(..4).and_then(|bytes| bytes.try_into().ok()) else {
        return false;
    };
    let length = u32::from_le_bytes(length) as usize;
    let Some(collection) = canonical.get(4..4_usize.saturating_add(length)) else { return false };
    collection == WEBHOOK_ENDPOINTS_COLLECTION.as_bytes()
        || collection == WEBHOOK_DELIVERIES_COLLECTION.as_bytes()
}

impl WebhookStorage for EngineWebhookStorage {
    fn create_endpoint(
        &self,
        scope: &TenantScope,
        endpoint: &str,
        now: u64,
    ) -> Result<WebhookEndpoint, StorageError> {
        let _guard = self.writes.lock().map_err(|_| StorageError::Unavailable)?;
        let record = WebhookEndpoint {
            id: terrabase::identity::process_generator().generate().to_text(),
            endpoint: endpoint.to_owned(),
            created_at: now,
            deleted: false,
        };
        self.write_document(
            scope,
            WEBHOOK_ENDPOINTS_COLLECTION,
            &record.id,
            endpoint_value(&record),
            ExpectedVersion::New,
        )?;
        Ok(record)
    }

    fn delete_endpoint(
        &self,
        scope: &TenantScope,
        id: &str,
    ) -> Result<WebhookEndpoint, StorageError> {
        let _guard = self.writes.lock().map_err(|_| StorageError::Unavailable)?;
        let document = self
            .read_document(scope, WEBHOOK_ENDPOINTS_COLLECTION, id)?
            .ok_or(StorageError::NotFound)?;
        let mut record = endpoint_from_document(&document).ok_or(StorageError::Unavailable)?;
        if !record.deleted {
            record.deleted = true;
            self.write_document(
                scope,
                WEBHOOK_ENDPOINTS_COLLECTION,
                id,
                endpoint_value(&record),
                ExpectedVersion::Exact(document.metadata().version()),
            )?;
        }
        Ok(record)
    }

    fn endpoint(
        &self,
        scope: &TenantScope,
        id: &str,
    ) -> Result<Option<WebhookEndpoint>, StorageError> {
        match self.read_document(scope, WEBHOOK_ENDPOINTS_COLLECTION, id)? {
            Some(document) => {
                endpoint_from_document(&document).map(Some).ok_or(StorageError::Unavailable)
            }
            None => Ok(None),
        }
    }

    fn list_endpoints(
        &self,
        scope: &TenantScope,
        include_deleted: bool,
    ) -> Result<Vec<WebhookEndpoint>, StorageError> {
        let documents = self.documents(scope, WEBHOOK_ENDPOINTS_COLLECTION)?;
        let mut records = Vec::with_capacity(documents.len());
        for document in &documents {
            let record = endpoint_from_document(document).ok_or(StorageError::Unavailable)?;
            if include_deleted || !record.deleted {
                records.push(record);
            }
        }
        records.sort_by(|left, right| left.id.cmp(&right.id));
        Ok(records)
    }

    fn ensure_delivery(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        publication: &Publication,
        now: u64,
    ) -> Result<DeliveryRecord, StorageError> {
        let _guard = self.writes.lock().map_err(|_| StorageError::Unavailable)?;
        let id = delivery_id(endpoint_id, &publication.event_id);
        if let Some(document) = self.read_document(scope, WEBHOOK_DELIVERIES_COLLECTION, &id)? {
            let record = delivery_from_document(&document).ok_or(StorageError::Unavailable)?;
            if record.event_id != publication.event_id || record.revision != publication.revision {
                return Err(StorageError::Conflict);
            }
            return Ok(record);
        }
        let record = DeliveryRecord {
            id: id.clone(),
            event_id: publication.event_id.clone(),
            revision: publication.revision,
            attempts: 0,
            next_attempt_at: now,
            retry_started_at: now,
            status: None,
            delivered: false,
            terminal: false,
        };
        self.write_document(
            scope,
            WEBHOOK_DELIVERIES_COLLECTION,
            &id,
            delivery_value(&record),
            ExpectedVersion::New,
        )?;
        Ok(record)
    }

    fn record_attempt(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        expected: &DeliveryRecord,
        attempt: &DeliveryAttempt,
    ) -> Result<(), StorageError> {
        let delivery_id = &expected.id;
        let DeliveryAttempt { attempts, next_attempt_at, status, delivered, terminal } = *attempt;
        let _guard = self.writes.lock().map_err(|_| StorageError::Unavailable)?;
        if !delivery_id.starts_with(&format!("{endpoint_id}:")) {
            return Err(StorageError::NotFound);
        }
        let document = self
            .read_document(scope, WEBHOOK_DELIVERIES_COLLECTION, delivery_id)?
            .ok_or(StorageError::NotFound)?;
        let mut record = delivery_from_document(&document).ok_or(StorageError::Unavailable)?;
        if attempts <= record.attempts {
            let persisted = DeliveryAttempt {
                attempts: record.attempts,
                next_attempt_at: record.next_attempt_at,
                status: record.status,
                delivered: record.delivered,
                terminal: record.terminal,
            };
            return if *attempt == persisted && record.retry_started_at == expected.retry_started_at
            {
                Ok(())
            } else {
                Err(StorageError::Conflict)
            };
        }
        if record != *expected || record.terminal || attempts != record.attempts.saturating_add(1) {
            return Err(StorageError::Conflict);
        }
        record.attempts = attempts;
        record.next_attempt_at = next_attempt_at;
        record.status = status;
        record.delivered = delivered;
        record.terminal = terminal;
        self.write_document(
            scope,
            WEBHOOK_DELIVERIES_COLLECTION,
            delivery_id,
            delivery_value(&record),
            ExpectedVersion::Exact(document.metadata().version()),
        )
    }

    fn expire_delivery(
        &self,
        scope: &TenantScope,
        expected: &DeliveryRecord,
        now: u64,
    ) -> Result<(), StorageError> {
        let _guard = self.writes.lock().map_err(|_| StorageError::Unavailable)?;
        let document = self
            .read_document(scope, WEBHOOK_DELIVERIES_COLLECTION, &expected.id)?
            .ok_or(StorageError::NotFound)?;
        let mut record = delivery_from_document(&document).ok_or(StorageError::Unavailable)?;
        if record != *expected || !retry_expired(record.retry_started_at, now) {
            return Err(StorageError::Conflict);
        }
        record.terminal = true;
        record.next_attempt_at = 0;
        self.write_document(
            scope,
            WEBHOOK_DELIVERIES_COLLECTION,
            &record.id,
            delivery_value(&record),
            ExpectedVersion::Exact(document.metadata().version()),
        )
    }

    fn delivery_log(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        limit: usize,
    ) -> Result<DeliveryLog, StorageError> {
        let prefix = format!("{endpoint_id}:");
        let documents = self.documents(scope, WEBHOOK_DELIVERIES_COLLECTION)?;
        let mut records = Vec::with_capacity(documents.len());
        for document in &documents {
            let record = delivery_from_document(document).ok_or(StorageError::Unavailable)?;
            if record.id.starts_with(&prefix) {
                records.push(record);
            }
        }
        records.sort_by(|left, right| left.id.cmp(&right.id));
        records.truncate(limit.min(MAX_JOURNAL_PAGE));
        Ok(DeliveryLog { deliveries: records })
    }

    fn due_deliveries(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        now: u64,
        limit: usize,
    ) -> Result<Vec<DeliveryRecord>, StorageError> {
        self.engine_scope(scope)?;
        if limit == 0 {
            return Ok(Vec::new());
        }
        let prefix = format!("{endpoint_id}:");
        let mut records = Vec::new();
        for document in self.documents(scope, WEBHOOK_DELIVERIES_COLLECTION)? {
            let record = delivery_from_document(&document).ok_or(StorageError::Unavailable)?;
            if record.id.starts_with(&prefix)
                && !record.delivered
                && !record.terminal
                && record.next_attempt_at <= now
            {
                records.push(record);
            }
        }
        records.sort_by(|left, right| {
            (left.next_attempt_at, &left.id).cmp(&(right.next_attempt_at, &right.id))
        });
        records.truncate(limit.min(MAX_JOURNAL_PAGE));
        Ok(records)
    }

    fn replay_delivery(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
        now: u64,
    ) -> Result<DeliveryRecord, StorageError> {
        let _guard = self.writes.lock().map_err(|_| StorageError::Unavailable)?;
        if !delivery_id.starts_with(&format!("{endpoint_id}:")) {
            return Err(StorageError::NotFound);
        }
        let document = self
            .read_document(scope, WEBHOOK_DELIVERIES_COLLECTION, delivery_id)?
            .ok_or(StorageError::NotFound)?;
        let mut record = delivery_from_document(&document).ok_or(StorageError::Unavailable)?;
        record.next_attempt_at = now;
        record.retry_started_at = now;
        record.delivered = false;
        record.terminal = false;
        self.write_document(
            scope,
            WEBHOOK_DELIVERIES_COLLECTION,
            delivery_id,
            delivery_value(&record),
            ExpectedVersion::Exact(document.metadata().version()),
        )?;
        Ok(record)
    }
}

/// Placement-aware secret port.  Secret bytes must live in the configured
/// secret manager/Vault and never in endpoint rows or this service.
pub trait WebhookSecrets: Send + Sync + 'static {
    /// Provisions a secret after durable endpoint creation.
    ///
    /// # Errors
    ///
    /// Returns an unavailable secret provider or an endpoint/placement refusal.
    fn provision(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        region: &str,
    ) -> Result<(), SecretError>;
    /// Destroys a secret after durable endpoint deletion.
    ///
    /// # Errors
    ///
    /// Returns an unavailable provider or missing secret error.
    fn destroy(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        region: &str,
    ) -> Result<(), SecretError>;
    /// Reads the secret only in an admitted placement.
    ///
    /// # Errors
    ///
    /// Returns an unavailable provider or missing secret error.
    fn get(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        region: &str,
    ) -> Result<Vec<u8>, SecretError>;
}

/// D-64 endpoint guard port.  The implementation may adapt
/// [`crate::guards::AbuseGuards`].
pub trait WebhookGuard: Send + Sync + 'static {
    /// Reserves one endpoint identity.  Called only after durable creation.
    ///
    /// # Errors
    ///
    /// Returns an error when the endpoint cannot be admitted under its scope and quota.
    fn reserve(
        &self,
        scope: &TenantScope,
        endpoint: &str,
    ) -> Result<(), crate::guards::GuardRejection>;
    /// Releases an endpoint identity after durable deletion.
    fn release(&self, scope: &TenantScope, endpoint: &str);
}

/// Adapter for the member's D-64 abuse guard.
pub struct AbuseGuardWebhookGuard {
    guards: Arc<AbuseGuards>,
}

impl AbuseGuardWebhookGuard {
    /// Binds webhook reservations to the shared member guard state.
    #[must_use]
    pub fn new(guards: Arc<AbuseGuards>) -> Self {
        Self { guards }
    }
}

impl WebhookGuard for AbuseGuardWebhookGuard {
    fn reserve(
        &self,
        scope: &TenantScope,
        endpoint: &str,
    ) -> Result<(), crate::guards::GuardRejection> {
        self.guards.register_webhook(&scope.database, endpoint)
    }

    fn release(&self, scope: &TenantScope, endpoint: &str) {
        self.guards.unregister_webhook(&scope.database, endpoint);
    }
}

/// Network egress port.  It owns DNS resolution and must reject every private,
/// loopback, link-local, metadata, and otherwise non-public destination after
/// resolution.  This explicit check prevents a DNS-to-private SSRF shortcut.
pub trait WebhookDelivery: Send + Sync + 'static {
    /// Validates the target and its resolved address policy before create.
    ///
    /// # Errors
    ///
    /// Returns target-policy, DNS, or transport failures.
    fn validate_target(&self, endpoint: &str) -> Result<(), DeliveryError>;
    /// Sends one signed request with bounded transport timeouts.
    ///
    /// # Errors
    ///
    /// Returns a rejected target, retryable network error, or permanent delivery failure.
    fn send(
        &self,
        endpoint: &str,
        headers: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError>;
}

/// TLS-capable egress hook used by [`HttpsWebhookDelivery`].
///
/// The server crate intentionally does not ship a second HTTP client. A
/// member host supplies the audited HTTPS implementation (certificate and
/// hostname validation, connect/read deadlines, and bounded response reads),
/// while this adapter owns the DNS rebinding/SSRF admission check.
pub trait HttpsTransport: Send + Sync + 'static {
    /// Resolves the endpoint immediately before use. Implementations must not
    /// return cached addresses beyond the request's lifetime.
    ///
    /// # Errors
    ///
    /// Returns a target-policy or DNS-resolution failure.
    fn resolve(&self, endpoint: &str) -> Result<Vec<IpAddr>, DeliveryError>;
    /// Sends one HTTPS request using the already validated endpoint.
    ///
    /// # Errors
    ///
    /// Returns a rejected target, retryable network error, or permanent delivery failure.
    fn send_https(
        &self,
        endpoint: &str,
        addresses: &[IpAddr],
        headers: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError>;
}

/// SSRF-safe adapter around the host's bounded HTTPS transport.
pub struct HttpsWebhookDelivery<T> {
    transport: Arc<T>,
}

impl<T> HttpsWebhookDelivery<T> {
    /// Installs an explicit HTTPS provider. There is no direct URL or memory
    /// transport fallback.
    #[must_use]
    pub fn new(transport: Arc<T>) -> Self {
        Self { transport }
    }
}

impl<T: HttpsTransport> WebhookDelivery for HttpsWebhookDelivery<T> {
    fn validate_target(&self, endpoint: &str) -> Result<(), DeliveryError> {
        let endpoint = validate_endpoint(endpoint).map_err(|_| DeliveryError::TargetRejected)?;
        let addresses = self.transport.resolve(&endpoint)?;
        if addresses.is_empty() || addresses.iter().any(|address| !is_public_ip(*address)) {
            return Err(DeliveryError::TargetRejected);
        }
        Ok(())
    }

    fn send(
        &self,
        endpoint: &str,
        headers: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError> {
        // Re-resolve for every attempt. This closes the DNS-rebinding window
        // between endpoint creation and a later retry.
        let endpoint = validate_endpoint(endpoint).map_err(|_| DeliveryError::TargetRejected)?;
        let addresses = self.transport.resolve(&endpoint)?;
        if addresses.is_empty() || addresses.iter().any(|address| !is_public_ip(*address)) {
            return Err(DeliveryError::TargetRejected);
        }
        self.transport.send_https(&endpoint, &addresses, headers, body)
    }
}

/// The five production ports required by the webhook service.
///
/// Ports are optional at this composition boundary deliberately: a member can
/// be assembled before all of its control-plane integrations are available.
/// [`WebhookService::from_ports`] refuses the missing-port case, so an absent
/// provider can never turn into an in-memory or direct-client fallback.
#[derive(Default)]
pub struct WebhookPorts {
    /// Durable endpoint and delivery rows.
    pub storage: Option<Arc<dyn WebhookStorage>>,
    /// Engine publication journal.
    pub journal: Option<Arc<dyn WebhookJournal>>,
    /// Placement-aware secret manager.
    pub secrets: Option<Arc<dyn WebhookSecrets>>,
    /// D-64 endpoint guard.
    pub guard: Option<Arc<dyn WebhookGuard>>,
    /// HTTPS egress transport.
    pub delivery: Option<Arc<dyn WebhookDelivery>>,
}

impl WebhookPorts {
    /// Installs all five production ports in one explicit bundle.
    #[must_use]
    pub fn production(
        storage: Arc<dyn WebhookStorage>,
        journal: Arc<dyn WebhookJournal>,
        secrets: Arc<dyn WebhookSecrets>,
        guard: Arc<dyn WebhookGuard>,
        delivery: Arc<dyn WebhookDelivery>,
    ) -> Self {
        Self {
            storage: Some(storage),
            journal: Some(journal),
            secrets: Some(secrets),
            guard: Some(guard),
            delivery: Some(delivery),
        }
    }
}

/// A production port that was absent during composition.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WebhookPort {
    /// Durable endpoint and delivery rows.
    Storage,
    /// Engine publication journal.
    Journal,
    /// Placement-aware secret manager.
    Secrets,
    /// D-64 endpoint guard.
    Guard,
    /// HTTPS egress transport.
    Delivery,
}

impl WebhookPort {
    /// Stable operator-facing port name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Storage => "storage",
            Self::Journal => "journal",
            Self::Secrets => "secrets",
            Self::Guard => "guard",
            Self::Delivery => "delivery",
        }
    }
}

/// Failure to assemble a handler-ready production webhook service.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WebhookCompositionError {
    /// One required port was not installed.
    MissingPort(WebhookPort),
    /// The placement region was empty.
    EmptyRegion,
}

impl fmt::Display for WebhookCompositionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingPort(port) => {
                write!(formatter, "webhook {} port is unavailable", port.as_str())
            }
            Self::EmptyRegion => formatter.write_str("webhook placement region is empty"),
        }
    }
}

impl std::error::Error for WebhookCompositionError {}

/// Network response used to classify retry behavior.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DeliveryResponse {
    /// HTTP response status.
    pub status: u16,
}

/// Injectable time source for retry scheduling and deterministic tests.
pub trait WebhookClock: Send + Sync + 'static {
    /// Current unix seconds.
    fn now(&self) -> u64;
}

/// System clock used by production composition.
#[derive(Clone, Copy, Debug, Default)]
pub struct SystemWebhookClock;

impl WebhookClock for SystemWebhookClock {
    fn now(&self) -> u64 {
        SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |duration| duration.as_secs())
    }
}

/// Authorization callback for the HTTP adapter.  Parent authentication must
/// provide this; absent or failing authorization denies the request.
pub trait WebhookAuthorizer: Send + Sync + 'static {
    /// Checks the authenticated tenant/project and requested action.
    ///
    /// # Errors
    ///
    /// Returns an authentication, scope, or authorization refusal.
    fn authorize(
        &self,
        headers: &HeaderMap,
        method: &Method,
        database: &str,
    ) -> Result<TenantScope, WebhookError>;
}

fn authorize_scope(
    authorizer: &dyn WebhookAuthorizer,
    headers: &HeaderMap,
    method: &Method,
    database: &str,
) -> Result<TenantScope, WebhookError> {
    let scope = authorizer.authorize(headers, method, database)?;
    if scope.database == database { Ok(scope) } else { Err(WebhookError::forbidden()) }
}

/// Durable webhook service composed from explicit production ports.
#[derive(Clone)]
pub struct WebhookService {
    storage: Arc<dyn WebhookStorage>,
    journal: Arc<dyn WebhookJournal>,
    secrets: Arc<dyn WebhookSecrets>,
    guard: Arc<dyn WebhookGuard>,
    delivery: Arc<dyn WebhookDelivery>,
    clock: Arc<dyn WebhookClock>,
    region: String,
}

impl WebhookService {
    /// Builds the service.  All ports and a non-empty placement region are
    /// mandatory, so a production caller cannot accidentally use a fallback.
    ///
    /// # Errors
    ///
    /// Returns an invalid-request error if the placement region is empty.
    pub fn new(
        storage: Arc<dyn WebhookStorage>,
        journal: Arc<dyn WebhookJournal>,
        secrets: Arc<dyn WebhookSecrets>,
        guard: Arc<dyn WebhookGuard>,
        delivery: Arc<dyn WebhookDelivery>,
        clock: Arc<dyn WebhookClock>,
        region: impl Into<String>,
    ) -> Result<Self, WebhookError> {
        let region = region.into();
        if region.is_empty() {
            return Err(WebhookError::misconfigured());
        }
        Ok(Self { storage, journal, secrets, guard, delivery, clock, region })
    }

    /// Composes a service from explicit production ports.
    ///
    /// Every required dependency is checked before the service is returned;
    /// callers get the exact missing hook and can keep the member unready.
    ///
    /// # Errors
    ///
    /// Returns the missing-port identity or an empty-region composition error.
    pub fn from_ports(
        ports: WebhookPorts,
        clock: Arc<dyn WebhookClock>,
        region: impl Into<String>,
    ) -> Result<Arc<Self>, WebhookCompositionError> {
        let storage =
            ports.storage.ok_or(WebhookCompositionError::MissingPort(WebhookPort::Storage))?;
        let journal =
            ports.journal.ok_or(WebhookCompositionError::MissingPort(WebhookPort::Journal))?;
        let secrets =
            ports.secrets.ok_or(WebhookCompositionError::MissingPort(WebhookPort::Secrets))?;
        let guard = ports.guard.ok_or(WebhookCompositionError::MissingPort(WebhookPort::Guard))?;
        let delivery =
            ports.delivery.ok_or(WebhookCompositionError::MissingPort(WebhookPort::Delivery))?;
        let region = region.into();
        if region.is_empty() {
            return Err(WebhookCompositionError::EmptyRegion);
        }
        Ok(Arc::new(Self { storage, journal, secrets, guard, delivery, clock, region }))
    }

    /// Creates an HTTPS endpoint.  Durable create precedes secret provisioning
    /// and D-64 reservation; later failures trigger best-effort cleanup.
    ///
    /// # Errors
    ///
    /// Returns endpoint-policy, transport, durable storage, secret provisioning, or
    /// quota-admission failures. Cleanup errors do not replace the original failure.
    pub fn create(
        &self,
        scope: &TenantScope,
        input: &CreateWebhook,
    ) -> Result<WebhookEndpoint, WebhookError> {
        let endpoint = validate_endpoint(&input.endpoint)?;
        self.delivery.validate_target(&endpoint).map_err(WebhookError::delivery)?;
        let record = self
            .storage
            .create_endpoint(scope, &endpoint, self.clock.now())
            .map_err(WebhookError::storage)?;
        if let Err(error) = self.secrets.provision(scope, &record.id, &self.region) {
            let _ = self.storage.delete_endpoint(scope, &record.id);
            return Err(WebhookError::secret(error));
        }
        if self.guard.reserve(scope, &record.endpoint).is_err() {
            let _ = self.secrets.destroy(scope, &record.id, &self.region);
            let _ = self.storage.delete_endpoint(scope, &record.id);
            return Err(WebhookError::guard());
        }
        Ok(record)
    }

    /// Lists durable endpoints, omitting deleted records by default.
    ///
    /// # Errors
    ///
    /// Returns the durable storage failure as a webhook service error.
    pub fn list(&self, scope: &TenantScope) -> Result<Vec<WebhookEndpoint>, WebhookError> {
        self.storage.list_endpoints(scope, false).map_err(WebhookError::storage)
    }

    /// Deletes one endpoint durably and then releases its D-64 reservation.
    ///
    /// # Errors
    ///
    /// Returns endpoint lookup/deletion or secret destruction failures.
    pub fn delete(&self, scope: &TenantScope, id: &str) -> Result<WebhookEndpoint, WebhookError> {
        let endpoint = self
            .storage
            .endpoint(scope, id)
            .map_err(WebhookError::storage)?
            .ok_or_else(WebhookError::not_found)?;
        let deleted = self.storage.delete_endpoint(scope, id).map_err(WebhookError::storage)?;
        self.guard.release(scope, &endpoint.endpoint);
        let _ = self.secrets.destroy(scope, id, &self.region);
        Ok(deleted)
    }

    /// Replays one durable delivery immediately, then performs it once.
    ///
    /// # Errors
    ///
    /// Returns durable replay, endpoint, secret, publication, or attempt-recording failures.
    pub async fn replay(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
    ) -> Result<DeliveryRecord, WebhookError> {
        let service = self.clone();
        let scope = scope.clone();
        let endpoint_id = endpoint_id.to_owned();
        let delivery_id = delivery_id.to_owned();
        tokio::task::spawn_blocking(move || {
            service.replay_blocking(&scope, &endpoint_id, &delivery_id)
        })
        .await
        .map_err(|_| WebhookError::storage(StorageError::Unavailable))?
    }

    fn replay_blocking(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
    ) -> Result<DeliveryRecord, WebhookError> {
        let now = self.clock.now();
        let record = self
            .storage
            .replay_delivery(scope, endpoint_id, delivery_id, now)
            .map_err(WebhookError::storage)?;
        self.attempt(scope, endpoint_id, &record, now)?;
        self.storage
            .delivery_log(scope, endpoint_id, MAX_JOURNAL_PAGE)
            .map_err(WebhookError::storage)
            .and_then(|log| {
                log.deliveries
                    .into_iter()
                    .find(|item| item.id == record.id)
                    .ok_or_else(WebhookError::not_found)
            })
    }

    /// Consumes one durable journal page, creating idempotent delivery rows and
    /// attempting a bounded page of due rows, including earlier publications.
    ///
    /// # Errors
    ///
    /// Returns endpoint/journal read, delivery creation, secret, or attempt-recording failures.
    pub async fn dispatch(
        &self,
        scope: &TenantScope,
        cursor: u64,
    ) -> Result<DispatchReport, WebhookError> {
        let service = self.clone();
        let scope = scope.clone();
        tokio::task::spawn_blocking(move || service.dispatch_blocking(&scope, cursor))
            .await
            .map_err(|_| WebhookError::storage(StorageError::Unavailable))?
    }

    fn dispatch_blocking(
        &self,
        scope: &TenantScope,
        cursor: u64,
    ) -> Result<DispatchReport, WebhookError> {
        let endpoints = self.list(scope)?;
        let publications =
            self.journal.page(scope, cursor, MAX_JOURNAL_PAGE).map_err(WebhookError::storage)?;
        let mut report = DispatchReport::default();
        for endpoint in endpoints {
            if endpoint.deleted {
                continue;
            }
            for publication in &publications {
                if publication.body.len() > MAX_DELIVERY_BYTES {
                    report.skipped = report.skipped.saturating_add(1);
                    continue;
                }
                self.storage
                    .ensure_delivery(scope, &endpoint.id, publication, self.clock.now())
                    .map_err(WebhookError::storage)?;
            }
            let now = self.clock.now();
            let due = self
                .storage
                .due_deliveries(scope, &endpoint.id, now, MAX_JOURNAL_PAGE)
                .map_err(WebhookError::storage)?;
            for row in due {
                if self.attempt(scope, &endpoint.id, &row, self.clock.now())? {
                    report.attempted = report.attempted.saturating_add(1);
                }
            }
        }
        report.publications = publications.len();
        Ok(report)
    }

    /// Reads the durable delivery log for dashboard inspection.
    ///
    /// # Errors
    ///
    /// Returns the durable delivery-log read failure as a service error.
    pub fn delivery_log(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        limit: usize,
    ) -> Result<DeliveryLog, WebhookError> {
        self.storage
            .delivery_log(scope, endpoint_id, limit.min(MAX_JOURNAL_PAGE))
            .map_err(WebhookError::storage)
    }

    fn attempt(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        row: &DeliveryRecord,
        now: u64,
    ) -> Result<bool, WebhookError> {
        if retry_expired(row.retry_started_at, now) {
            self.storage.expire_delivery(scope, row, now).map_err(WebhookError::storage)?;
            return Ok(false);
        }
        let endpoint = self
            .storage
            .endpoint(scope, endpoint_id)
            .map_err(WebhookError::storage)?
            .ok_or_else(WebhookError::not_found)?;
        if endpoint.deleted {
            return Ok(false);
        }
        let secret =
            self.secrets.get(scope, endpoint_id, &self.region).map_err(WebhookError::secret)?;
        if secret.is_empty() {
            return Err(WebhookError::misconfigured());
        }
        let publication = self
            .journal
            .page(scope, row.revision.saturating_sub(1), MAX_JOURNAL_PAGE)
            .map_err(WebhookError::storage)?
            .into_iter()
            .find(|item| item.revision == row.revision && item.event_id == row.event_id)
            .ok_or_else(WebhookError::not_found)?;
        let timestamp = now.to_string();
        let signature = sign_delivery(&secret, &timestamp, &publication.body);
        let mut headers = BTreeMap::new();
        headers.insert("content-type".into(), "application/msgpack".into());
        headers.insert("user-agent".into(), "TerraScale-Webhook/1".into());
        headers.insert("x-terrascale-webhook-id".into(), endpoint.id.clone());
        headers.insert("x-terrascale-webhook-event".into(), publication.event_id.clone());
        headers.insert("x-terrascale-webhook-timestamp".into(), timestamp);
        headers.insert("x-terrascale-webhook-signature".into(), format!("v1={signature}"));
        headers.insert("idempotency-key".into(), row.id.clone());
        let result = self.delivery.send(&endpoint.endpoint, &headers, &publication.body);
        let attempt = row.attempts.saturating_add(1);
        let (status, delivered, terminal, next) = match result {
            Ok(response) if (200..300).contains(&response.status) => {
                (Some(response.status), true, true, 0)
            }
            Ok(response) => retry_state(response.status, attempt, row.retry_started_at, now),
            Err(DeliveryError::TargetRejected | DeliveryError::Permanent) => (None, false, true, 0),
            Err(DeliveryError::Retryable) => retry_state(0, attempt, row.retry_started_at, now),
        };
        self.storage
            .record_attempt(
                scope,
                endpoint_id,
                row,
                &DeliveryAttempt {
                    attempts: attempt,
                    next_attempt_at: next,
                    status,
                    delivered,
                    terminal,
                },
            )
            .map_err(WebhookError::storage)?;
        Ok(true)
    }
}

/// One dispatch result; delivery itself remains unbilled.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
pub struct DispatchReport {
    /// Publications read from the shared journal.
    pub publications: usize,
    /// Attempts issued to the delivery port.
    pub attempted: usize,
    /// Oversized publications skipped safely.
    pub skipped: usize,
}

/// Public HTTP state used by the parent API router.
#[derive(Clone)]
pub struct WebhookHttpState {
    /// Durable webhook service.
    pub service: Arc<WebhookService>,
    /// Parent authentication/scope adapter.
    pub authorizer: Arc<dyn WebhookAuthorizer>,
}

impl WebhookHttpState {
    /// Builds handler state only after every production dependency is present.
    ///
    /// # Errors
    ///
    /// Returns a missing-port or empty-region error from [`WebhookService::from_ports`].
    pub fn from_ports(
        ports: WebhookPorts,
        authorizer: Arc<dyn WebhookAuthorizer>,
        clock: Arc<dyn WebhookClock>,
        region: impl Into<String>,
    ) -> Result<Arc<Self>, WebhookCompositionError> {
        let service = WebhookService::from_ports(ports, clock, region)?;
        Ok(Arc::new(Self { service, authorizer }))
    }
}

/// Mounts list/create/delete plus delivery-log/replay routes into a parent
/// axum router.  The authorizer is mandatory and runs before any storage call.
#[must_use = "the returned router must be merged into the public API"]
pub fn router(service: Arc<WebhookService>, authorizer: Arc<dyn WebhookAuthorizer>) -> Router {
    let state = WebhookHttpState { service, authorizer };
    Router::new()
        .route("/api/v1/databases/{database}/webhooks", get(list_webhooks).post(create_webhook))
        .route("/api/v1/databases/{database}/webhooks/{id}", delete(delete_webhook))
        .route("/api/v1/databases/{database}/webhooks/{id}/deliveries", get(list_deliveries))
        .route(
            "/api/v1/databases/{database}/webhooks/{id}/deliveries/{delivery_id}/replay",
            post(replay_webhook),
        )
        .layer(axum::extract::DefaultBodyLimit::max(MAX_WEBHOOK_BODY_BYTES))
        .with_state(Arc::new(state))
}

/// Alias used by parent HTTP composition modules.
#[must_use = "the returned router must be merged into the public API"]
pub fn http_router(service: Arc<WebhookService>, authorizer: Arc<dyn WebhookAuthorizer>) -> Router {
    router(service, authorizer)
}

/// Builds the parent-router fragment and its state from explicit production
/// ports. An absent secret provider, journal, storage, guard, or transport is
/// reported before any route is returned.
///
/// # Errors
///
/// Returns a missing-port or empty-region composition error.
pub fn router_from_ports(
    ports: WebhookPorts,
    authorizer: Arc<dyn WebhookAuthorizer>,
    clock: Arc<dyn WebhookClock>,
    region: impl Into<String>,
) -> Result<Router, WebhookCompositionError> {
    let state = WebhookHttpState::from_ports(ports, authorizer, clock, region)?;
    Ok(Router::new()
        .route("/api/v1/databases/{database}/webhooks", get(list_webhooks).post(create_webhook))
        .route("/api/v1/databases/{database}/webhooks/{id}", delete(delete_webhook))
        .route("/api/v1/databases/{database}/webhooks/{id}/deliveries", get(list_deliveries))
        .route(
            "/api/v1/databases/{database}/webhooks/{id}/deliveries/{delivery_id}/replay",
            post(replay_webhook),
        )
        .layer(axum::extract::DefaultBodyLimit::max(MAX_WEBHOOK_BODY_BYTES))
        .with_state(state))
}

async fn blocking_webhook_response(work: impl FnOnce() -> Response + Send + 'static) -> Response {
    tokio::task::spawn_blocking(work)
        .await
        .unwrap_or_else(|_| WebhookError::storage(StorageError::Unavailable).into_response())
}

/// Parent-callable list handler.
pub async fn list_webhooks(
    State(state): State<Arc<WebhookHttpState>>,
    Path(database): Path<String>,
    headers: HeaderMap,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::GET, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    blocking_webhook_response(move || match state.service.list(&scope) {
        Ok(webhooks) => json_response(StatusCode::OK, &serde_json::json!({ "webhooks": webhooks })),
        Err(error) => error.into_response(),
    })
    .await
}

/// Parent-callable create handler.
pub async fn create_webhook(
    State(state): State<Arc<WebhookHttpState>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    body: Bytes,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::POST, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    let Ok(input) = serde_json::from_slice::<CreateWebhook>(&body) else {
        return WebhookError::invalid_request("webhook request is invalid").into_response();
    };
    blocking_webhook_response(move || match state.service.create(&scope, &input) {
        Ok(webhook) => json_response(StatusCode::CREATED, &webhook),
        Err(error) => error.into_response(),
    })
    .await
}

/// Parent-callable delete handler.
pub async fn delete_webhook(
    State(state): State<Arc<WebhookHttpState>>,
    Path((database, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let scope =
        match authorize_scope(state.authorizer.as_ref(), &headers, &Method::DELETE, &database) {
            Ok(scope) => scope,
            Err(error) => return error.into_response(),
        };
    blocking_webhook_response(move || match state.service.delete(&scope, &id) {
        Ok(webhook) => json_response(StatusCode::OK, &webhook),
        Err(error) => error.into_response(),
    })
    .await
}

/// Parent-callable delivery-log handler.
pub async fn list_deliveries(
    State(state): State<Arc<WebhookHttpState>>,
    Path((database, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::GET, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    blocking_webhook_response(move || {
        match state.service.delivery_log(&scope, &id, MAX_JOURNAL_PAGE) {
            Ok(log) => json_response(StatusCode::OK, &log),
            Err(error) => error.into_response(),
        }
    })
    .await
}

/// Parent-callable replay handler.
pub async fn replay_webhook(
    State(state): State<Arc<WebhookHttpState>>,
    Path((database, id, delivery_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::POST, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    match state.service.replay(&scope, &id, &delivery_id).await {
        Ok(record) => json_response(StatusCode::OK, &record),
        Err(error) => error.into_response(),
    }
}

fn json_response<T: Serialize>(status: StatusCode, value: &T) -> Response {
    match serde_json::to_string(value) {
        Ok(body) => (status, [(CONTENT_TYPE, HeaderValue::from_static("application/json"))], body)
            .into_response(),
        Err(_) => WebhookError::misconfigured().into_response(),
    }
}

/// Typed public failure.  Detail is intentionally not exposed in the HTTP
/// response, preventing endpoint, secret, and network oracle leakage.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WebhookError {
    code: &'static str,
    status: StatusCode,
    detail: &'static str,
}

impl WebhookError {
    /// Constructs a malformed-request failure for an authorization adapter.
    #[must_use]
    pub fn invalid_request(detail: &'static str) -> Self {
        Self { code: "invalid_request", status: StatusCode::BAD_REQUEST, detail }
    }
    /// Constructs an authentication failure for an authorization adapter.
    #[must_use]
    pub fn unauthorized() -> Self {
        Self {
            code: "unauthorized",
            status: StatusCode::UNAUTHORIZED,
            detail: "authorization failed",
        }
    }
    /// Constructs a scope failure for an authorization adapter.
    #[must_use]
    pub fn forbidden() -> Self {
        Self {
            code: "forbidden",
            status: StatusCode::FORBIDDEN,
            detail: "authorization scope does not admit this database",
        }
    }
    fn not_found() -> Self {
        Self { code: "not_found", status: StatusCode::NOT_FOUND, detail: "webhook not found" }
    }
    fn guard() -> Self {
        Self {
            code: "webhook_limit",
            status: StatusCode::TOO_MANY_REQUESTS,
            detail: "webhook endpoint budget exceeded",
        }
    }
    fn misconfigured() -> Self {
        Self {
            code: "webhook_unavailable",
            status: StatusCode::SERVICE_UNAVAILABLE,
            detail: "webhook dependencies unavailable",
        }
    }
    fn storage(_: StorageError) -> Self {
        Self::misconfigured()
    }
    fn secret(_: SecretError) -> Self {
        Self::misconfigured()
    }
    fn delivery(_: DeliveryError) -> Self {
        Self {
            code: "endpoint_rejected",
            status: StatusCode::UNPROCESSABLE_ENTITY,
            detail: "endpoint rejected by egress policy",
        }
    }

    /// Stable machine-readable error code.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        self.code
    }

    /// HTTP status returned by the adapter for this failure.
    #[must_use]
    pub const fn status(&self) -> StatusCode {
        self.status
    }
}

impl IntoResponse for WebhookError {
    fn into_response(self) -> Response {
        let body = serde_json::json!({ "error": { "code": self.code, "message": self.detail, "fix": "follow the webhook contract and retry when safe" } });
        json_response(self.status, &body)
    }
}

impl fmt::Display for WebhookError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.code)
    }
}

impl std::error::Error for WebhookError {}

/// Validates and canonicalizes an endpoint without performing DNS.  The
/// delivery port must perform the second, resolved-address policy check.
///
/// # Errors
///
/// Returns an invalid-request error for a malformed, oversized, non-HTTPS,
/// credential-bearing, or non-public endpoint.
pub fn validate_endpoint(raw: &str) -> Result<String, WebhookError> {
    let endpoint = raw.trim();
    if endpoint.len() > MAX_ENDPOINT_BYTES
        || endpoint.is_empty()
        || !endpoint.is_ascii()
        || endpoint.chars().any(char::is_control)
        || endpoint.contains('#')
    {
        return Err(WebhookError::invalid_request("endpoint is invalid"));
    }
    let Some(authority_and_path) = endpoint.strip_prefix("https://") else {
        return Err(WebhookError::invalid_request("endpoint must use HTTPS"));
    };
    if authority_and_path.is_empty()
        || authority_and_path.starts_with('/')
        || authority_and_path.starts_with('?')
        || authority_and_path.starts_with('#')
    {
        return Err(WebhookError::invalid_request("endpoint authority is missing"));
    }
    let authority_end =
        authority_and_path.find(['/', '?', '#']).unwrap_or(authority_and_path.len());
    let authority = &authority_and_path[..authority_end];
    if authority.is_empty() || authority.contains('@') || authority.contains(char::is_whitespace) {
        return Err(WebhookError::invalid_request("endpoint authority is invalid"));
    }
    let (host, port) = parse_authority(authority)?;
    if port == Some(0) {
        return Err(WebhookError::invalid_request("endpoint port is invalid"));
    }
    let host = host.to_ascii_lowercase();
    if host == "localhost"
        || host.ends_with(".localhost")
        || host.strip_suffix(".local").is_some()
        || host.ends_with(".internal")
        || host.ends_with(".home.arpa")
    {
        return Err(WebhookError::invalid_request("endpoint host is not public"));
    }
    if host.starts_with('.')
        || host.ends_with('.')
        || host.starts_with('-')
        || host.ends_with('-')
        || host.contains("..")
    {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    if let Ok(address) = host.parse::<IpAddr>()
        && !is_public_ip(address)
    {
        return Err(WebhookError::invalid_request("endpoint host is not public"));
    }
    if host.bytes().any(|byte| {
        !(byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'[' | b']' | b':'))
    }) {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    Ok(endpoint.to_owned())
}

fn parse_authority(authority: &str) -> Result<(&str, Option<u16>), WebhookError> {
    if let Some(rest) = authority.strip_prefix('[') {
        let end = rest
            .find(']')
            .ok_or_else(|| WebhookError::invalid_request("endpoint host is invalid"))?;
        let host = &rest[..end];
        if host.parse::<Ipv6Addr>().is_err() {
            return Err(WebhookError::invalid_request("endpoint host is invalid"));
        }
        let suffix = &rest[end + 1..];
        let port = if suffix.is_empty() {
            None
        } else {
            Some(
                suffix
                    .strip_prefix(':')
                    .ok_or_else(|| WebhookError::invalid_request("endpoint port is invalid"))?
                    .parse()
                    .map_err(|_| WebhookError::invalid_request("endpoint port is invalid"))?,
            )
        };
        return Ok((host, port));
    }
    let (host, port) = if let Some((host, port)) = authority.rsplit_once(':') {
        let port =
            port.parse().map_err(|_| WebhookError::invalid_request("endpoint port is invalid"))?;
        (host, Some(port))
    } else {
        (authority, None)
    };
    if host.is_empty() || host.contains(':') {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    if host.bytes().all(|byte| byte.is_ascii_digit() || byte == b'.') {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    Ok((host, port))
}

fn is_public_ip(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(ip) => {
            let octets = ip.octets();
            let shared = octets[0] == 100 && (64..=127).contains(&octets[1]);
            let benchmark = octets[0] == 198 && (18..=19).contains(&octets[1]);
            !shared
                && !benchmark
                && octets[0] != 0
                && octets[0] < 224
                && !(octets[0] == 192 && octets[1] == 0 && octets[2] == 0)
                && !ip.is_private()
                && !ip.is_loopback()
                && !ip.is_link_local()
                && !ip.is_unspecified()
                && !ip.is_broadcast()
                && !is_ipv4_documentation(ip)
        }
        IpAddr::V6(ip) => {
            if let Some(mapped) = ip.to_ipv4_mapped() {
                return is_public_ip(IpAddr::V4(mapped));
            }
            let segments = ip.segments();
            !ip.is_loopback()
                && !ip.is_unspecified()
                && (segments[0] & 0xfe00 != 0xfc00)
                && (segments[0] & 0xffc0 != 0xfe80)
                && !ip.is_multicast()
                && !is_ipv6_documentation(ip)
        }
    }
}

fn is_ipv4_documentation(ip: Ipv4Addr) -> bool {
    ip.octets()[0] == 192 && ip.octets()[1] == 0 && ip.octets()[2] == 2
        || ip.octets()[0] == 198 && ip.octets()[1] == 51 && ip.octets()[2] == 100
        || ip.octets()[0] == 203 && ip.octets()[1] == 0 && ip.octets()[2] == 113
}
fn is_ipv6_documentation(ip: Ipv6Addr) -> bool {
    ip.segments()[0] == 0x2001 && ip.segments()[1] == 0x0db8
}

/// Computes the `v1` delivery MAC over `timestamp + "." + body`.
///
/// BLAKE3 keyed mode is used because it is already part of the server's
/// audited dependency set.  The secret is first normalized to BLAKE3's
/// fixed-size key, allowing secret managers to return any high-entropy key
/// size without weakening the MAC.
#[must_use]
pub fn sign_delivery(secret: &[u8], timestamp: &str, body: &[u8]) -> String {
    let key = blake3::hash(secret);
    let mut message = Vec::with_capacity(timestamp.len() + 1 + body.len());
    message.extend_from_slice(timestamp.as_bytes());
    message.push(b'.');
    message.extend_from_slice(body);
    blake3::keyed_hash(key.as_bytes(), &message).to_hex().to_string()
}

fn retry_expired(started_at: u64, now: u64) -> bool {
    now.saturating_sub(started_at) >= RETRY_HORIZON_SECONDS
}

fn retry_state(
    status: u16,
    attempt: u32,
    started_at: u64,
    now: u64,
) -> (Option<u16>, bool, bool, u64) {
    if !matches!(status, 0 | 408 | 425 | 429) && !(500..600).contains(&status) {
        return (Some(status).filter(|value| *value != 0), false, true, 0);
    }
    if attempt >= MAX_ATTEMPTS {
        return (Some(status).filter(|value| *value != 0), false, true, 0);
    }
    let delay = 1_u64
        .checked_shl(attempt.saturating_sub(1))
        .unwrap_or(MAX_RETRY_DELAY_SECONDS)
        .min(MAX_RETRY_DELAY_SECONDS);
    let next = now.checked_add(delay);
    if next.is_none_or(|next| retry_expired(started_at, next)) {
        (Some(status).filter(|value| *value != 0), false, true, 0)
    } else {
        (Some(status).filter(|value| *value != 0), false, false, next.unwrap_or(now))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    #[test]
    fn endpoint_policy_rejects_ssrf_forms() {
        for endpoint in [
            "http://example.com/hook",
            "https://localhost/hook",
            "https://127.0.0.1/hook",
            "https://10.0.0.1/hook",
            "https://[::1]/hook",
            "https://169.254.169.254/hook",
            "https://user@example.com/hook",
            "https://example.com:0/hook",
        ] {
            assert!(validate_endpoint(endpoint).is_err(), "{endpoint} must fail");
        }
        assert!(validate_endpoint("https://hooks.example.com/events").is_ok());
    }

    #[test]
    fn retry_schedule_is_bounded_and_terminal() {
        let first = retry_state(503, 1, 100, 100);
        assert_eq!(first.3, 101);
        let last = retry_state(503, MAX_ATTEMPTS, 100, 100);
        assert!(last.2);
        assert!(retry_state(404, 1, 100, 100).2);
        let deadline = 100 + RETRY_HORIZON_SECONDS;
        assert!(!retry_state(503, 1, 100, deadline - 2).2);
        assert!(retry_state(503, 1, 100, deadline - 1).2);
        assert!(retry_expired(100, deadline));
        assert!(!retry_expired(100, 99));
        assert!(retry_state(503, 1, u64::MAX, u64::MAX).2);
    }

    #[test]
    fn signing_is_stable_and_body_bound() {
        let one = sign_delivery(b"secret", "42", br#"{"revision":1}"#);
        let two = sign_delivery(b"secret", "42", br#"{"revision":1}"#);
        assert_eq!(one, two);
        assert_eq!(one.len(), 64);
        assert_ne!(one, sign_delivery(b"other", "42", br#"{"revision":1}"#));
    }

    #[test]
    fn composition_refuses_the_first_missing_durable_port() {
        let result = WebhookService::from_ports(
            WebhookPorts::default(),
            Arc::new(SystemWebhookClock),
            "GRU",
        );
        assert!(matches!(result, Err(WebhookCompositionError::MissingPort(WebhookPort::Storage))));
    }

    struct TestHttpsTransport {
        addresses: Vec<IpAddr>,
        sent: Mutex<Vec<Vec<IpAddr>>>,
    }

    impl HttpsTransport for TestHttpsTransport {
        fn resolve(&self, _endpoint: &str) -> Result<Vec<IpAddr>, DeliveryError> {
            Ok(self.addresses.clone())
        }

        fn send_https(
            &self,
            _endpoint: &str,
            addresses: &[IpAddr],
            _headers: &BTreeMap<String, String>,
            _body: &[u8],
        ) -> Result<DeliveryResponse, DeliveryError> {
            self.sent.lock().expect("test transport lock").push(addresses.to_vec());
            Ok(DeliveryResponse { status: 204 })
        }
    }

    #[test]
    fn https_adapter_rejects_private_resolution_and_pins_public_resolution() {
        let private = Arc::new(TestHttpsTransport {
            addresses: vec!["10.0.0.1".parse().expect("ip")],
            sent: Mutex::new(Vec::new()),
        });
        let private_delivery = HttpsWebhookDelivery::new(private);
        assert_eq!(
            private_delivery.validate_target("https://hooks.example/events"),
            Err(DeliveryError::TargetRejected)
        );

        let public = Arc::new(TestHttpsTransport {
            addresses: vec!["1.1.1.1".parse().expect("ip")],
            sent: Mutex::new(Vec::new()),
        });
        let public_delivery = HttpsWebhookDelivery::new(Arc::clone(&public));
        let response = public_delivery
            .send("https://hooks.example/events", &BTreeMap::new(), b"body")
            .expect("public target");
        assert_eq!(response.status, 204);
        assert_eq!(public.sent.lock().expect("test transport lock").len(), 1);
    }

    #[test]
    fn https_adapter_rejects_nonpublic_dns_answers_before_sending() {
        for address in [
            "10.0.0.1",
            "172.16.0.1",
            "192.168.1.1",
            "127.0.0.1",
            "169.254.169.254",
            "100.64.0.1",
            "198.18.0.1",
            "192.0.0.1",
            "192.0.2.1",
            "198.51.100.1",
            "203.0.113.1",
            "0.1.2.3",
            "224.0.0.1",
            "240.0.0.1",
            "255.255.255.255",
            "::1",
            "::",
            "fc00::1",
            "fe80::1",
            "ff02::1",
            "2001:db8::1",
            "::ffff:10.0.0.1",
        ] {
            let transport = Arc::new(TestHttpsTransport {
                addresses: vec!["1.1.1.1".parse().expect("public"), address.parse().expect("ip")],
                sent: Mutex::new(Vec::new()),
            });
            let delivery = HttpsWebhookDelivery::new(Arc::clone(&transport));
            assert_eq!(
                delivery.send("https://hooks.example/events", &BTreeMap::new(), b"body"),
                Err(DeliveryError::TargetRejected),
                "rejects mixed public/nonpublic answers containing {address}"
            );
            assert!(transport.sent.lock().expect("test transport lock").is_empty());
        }
        for host in
            ["LOCALHOST", "hooks.LOCALHOST", "hooks.LOCAL", "hooks.INTERNAL", "hooks.HOME.ARPA"]
        {
            assert!(validate_endpoint(&format!("https://{host}/events")).is_err());
        }
    }

    fn assert_retry_expiry_and_replay(
        storage: &EngineWebhookStorage,
        scope: &TenantScope,
        endpoint: &str,
        original: &DeliveryRecord,
    ) {
        assert_eq!(
            storage.expire_delivery(scope, original, RETRY_HORIZON_SECONDS - 1),
            Err(StorageError::Conflict)
        );
        storage.expire_delivery(scope, original, RETRY_HORIZON_SECONDS).expect("expiry");
        let expired = storage.delivery_log(scope, endpoint, 10).expect("expired log");
        assert!(expired.deliveries[0].terminal);
        assert_eq!(expired.deliveries[0].attempts, original.attempts);
        assert_eq!(expired.deliveries[0].next_attempt_at, 0);
        assert_eq!(
            storage.record_attempt(
                scope,
                endpoint,
                original,
                &DeliveryAttempt {
                    attempts: original.attempts + 1,
                    next_attempt_at: 0,
                    status: Some(204),
                    delivered: true,
                    terminal: true,
                }
            ),
            Err(StorageError::Conflict)
        );
        let replay = storage
            .replay_delivery(scope, endpoint, &original.id, RETRY_HORIZON_SECONDS + 1)
            .expect("explicit replay opens a new window");
        assert_eq!(replay.retry_started_at, RETRY_HORIZON_SECONDS + 1);
        assert!(!replay.terminal);
        assert_eq!(
            storage.expire_delivery(scope, original, RETRY_HORIZON_SECONDS + 1),
            Err(StorageError::Conflict)
        );
        assert_eq!(
            storage.delivery_log(scope, endpoint, 10).expect("replay log").deliveries,
            vec![replay]
        );
    }

    #[test]
    fn engine_storage_round_trips_endpoint_and_delivery_rows() {
        let root = std::env::temp_dir().join(format!(
            "terrascale-webhook-{}",
            terrabase::identity::process_generator().generate().to_text()
        ));
        let engine = Arc::new(
            Engine::open(
                terrabase::EngineConfig::durable(root.clone()).expect("durable test location"),
            )
            .expect("durable engine"),
        );
        let owner = engine.database_owner();
        let scope = TenantScope::new(
            owner.tenant().to_text(),
            owner.project().to_text(),
            owner.database().to_text(),
        )
        .expect("owner scope");
        let storage = EngineWebhookStorage::new(Arc::clone(&engine));
        let endpoint =
            storage.create_endpoint(&scope, "https://hooks.example/events", 42).expect("endpoint");
        assert_eq!(storage.endpoint(&scope, &endpoint.id), Ok(Some(endpoint.clone())));
        let publication =
            Publication { revision: 7, event_id: "event-7".into(), body: b"body".to_vec() };
        let delivery =
            storage.ensure_delivery(&scope, &endpoint.id, &publication, 42).expect("delivery");
        assert_eq!(
            storage.ensure_delivery(&scope, &endpoint.id, &publication, 99),
            Ok(delivery.clone())
        );
        let successful = DeliveryAttempt {
            attempts: 1,
            next_attempt_at: 0,
            status: Some(204),
            delivered: true,
            terminal: true,
        };
        storage.record_attempt(&scope, &endpoint.id, &delivery, &successful).expect("attempt");
        storage
            .record_attempt(&scope, &endpoint.id, &delivery, &successful)
            .expect("identical retry");
        assert_eq!(
            storage.record_attempt(
                &scope,
                &endpoint.id,
                &delivery,
                &DeliveryAttempt { status: Some(500), delivered: false, ..successful }
            ),
            Err(StorageError::Conflict)
        );
        let persisted = storage.delivery_log(&scope, &endpoint.id, 10).expect("unchanged log");
        assert!(persisted.deliveries[0].delivered);
        assert_eq!(persisted.deliveries[0].status, Some(204));
        assert_eq!(
            storage.delivery_log(&scope, &endpoint.id, 10).expect("log").deliveries.len(),
            1
        );
        let mut boundary_records = Vec::new();
        for value in [0, i64::MAX.cast_unsigned(), i64::MAX.cast_unsigned() + 1, u64::MAX] {
            let endpoint = storage
                .create_endpoint(&scope, "https://hooks.example/events", value)
                .expect("full-range endpoint timestamp");
            let publication = Publication {
                revision: value,
                event_id: format!("event-{value}"),
                body: b"body".to_vec(),
            };
            let delivery = storage
                .ensure_delivery(&scope, &endpoint.id, &publication, value)
                .expect("full-range delivery metadata");
            boundary_records.push((endpoint, delivery));
        }
        drop(storage);
        let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
        engine.shutdown().expect("flush");
        let reopened = Arc::new(
            Engine::open(terrabase::EngineConfig::durable(root.clone()).expect("config"))
                .expect("reopen"),
        );
        let storage = EngineWebhookStorage::new(Arc::clone(&reopened));
        for (endpoint, delivery) in boundary_records {
            assert_eq!(storage.endpoint(&scope, &endpoint.id), Ok(Some(endpoint.clone())));
            let log = storage.delivery_log(&scope, &endpoint.id, 10).expect("durable delivery");
            assert_eq!(log.deliveries, vec![delivery.clone()]);
            if delivery.retry_started_at == 0 {
                assert_retry_expiry_and_replay(&storage, &scope, &endpoint.id, &delivery);
            }
        }
        drop(storage);
        let Ok(reopened) = Arc::try_unwrap(reopened) else { panic!("sole reopened engine") };
        reopened.shutdown().expect("close reopened engine");
        let _ = std::fs::remove_dir_all(root);
    }
}

#[cfg(test)]
mod delivery_tests;
