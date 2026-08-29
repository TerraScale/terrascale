//! The composed Member: the engine and the CRDT plane in one server
//! (P2 composition).
//!
//! A [`ComposedMember`] is what "Loro leaderless multi-writer between
//! servers" means concretely: one Member whose accepted writes land in
//! its durable engine AND its own-peer [`ReplicaDoc`], and whose
//! anti-entropy with any other Member heals both planes — the CRDTs
//! merge causally, then each Member's engine is reconciled to the
//! merged CRDT state, so every server serves the same converged state
//! without an election, a leader, or a redirect.
//!
//! The reconcile path is idempotent per merged state: its engine
//! operations carry identities derived from the document and the
//! replica's frontier fingerprint, so re-running a heal at an unchanged
//! frontier replays the recorded receipts and commits nothing new.
//!
//! Honest limits of this landing: the CRDT state is in-memory (its
//! durable staging through the object-store tier lands with the
//! transport wiring), and reconcile enumerates the documents the Member
//! knows — durable re-enumeration after a restart needs the public
//! tenant-read surface that lands with P4.

use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;

use loro::LoroValue;
use terrabase_v2::engine::{CommitOutcome, Engine, EngineConfig};
use terrabase_v2::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};

use crate::{DOCS_ROOT, ReplicaDoc, ReplicaError, ReplicaExchange, ReplicaFrontier};

/// The single collection the composed plane materializes into.
const COLLECTION: &str = "documents";

/// Why a composed-member operation failed. Typed and bounded: no
/// engine internals cross the seam.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ComposeError {
    /// The engine refused the local write or read.
    EngineRefused,
    /// The CRDT plane refused.
    Replica(ReplicaError),
    /// A merged value has no engine representation.
    UnsupportedValue,
    /// An identifier was invalid.
    InvalidIdentifier,
}

/// What one reconcile pass changed in the member's engine.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ReconcileReport {
    puts: u64,
    deletes: u64,
}

impl ReconcileReport {
    /// Returns the number of documents reconciled forward.
    #[must_use]
    pub const fn puts(&self) -> u64 {
        self.puts
    }

    /// Returns the number of documents reconciled to deleted.
    #[must_use]
    pub const fn deletes(&self) -> u64 {
        self.deletes
    }
}

/// FNV-1a over bytes — a stable, dependency-free fingerprint for
/// deterministic reconcile identities.
fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    hash
}

/// Converts one engine value into its CRDT form.
fn to_loro(value: &DocumentValue) -> LoroValue {
    match value {
        DocumentValue::Null => LoroValue::Null,
        DocumentValue::Boolean(flag) => LoroValue::from(*flag),
        DocumentValue::Integer(number) => LoroValue::from(*number),
        DocumentValue::Number(number) => LoroValue::from(*number),
        DocumentValue::String(text) => LoroValue::from(text.as_str()),
        DocumentValue::Array(items) => {
            LoroValue::List(items.iter().map(to_loro).collect::<Vec<_>>().into())
        }
        DocumentValue::Object(fields) => LoroValue::Map(
            fields
                .iter()
                .map(|(key, field)| (key.clone(), to_loro(field)))
                .collect::<Vec<_>>()
                .into(),
        ),
    }
}

/// Converts one merged CRDT value into its engine form.
fn from_loro(value: LoroValue) -> Result<DocumentValue, ComposeError> {
    match value {
        LoroValue::Null => Ok(DocumentValue::Null),
        LoroValue::Bool(flag) => Ok(DocumentValue::Boolean(flag)),
        LoroValue::Double(number) => Ok(DocumentValue::Number(number)),
        LoroValue::I64(number) => Ok(DocumentValue::Integer(number)),
        LoroValue::String(text) => Ok(DocumentValue::String(text.to_string())),
        LoroValue::List(items) => {
            let mut converted = Vec::new();
            for item in items.unwrap() {
                converted.push(from_loro(item)?);
            }
            Ok(DocumentValue::Array(converted))
        }
        LoroValue::Map(fields) => {
            let mut converted = BTreeMap::new();
            for (field, value) in fields.unwrap() {
                converted.insert(field, from_loro(value)?);
            }
            Ok(DocumentValue::Object(converted))
        }
        LoroValue::Binary(_) | LoroValue::Container(_) => Err(ComposeError::UnsupportedValue),
    }
}

/// Extracts the merged documents of a whole CRDT scope.
fn crdt_documents(
    state: &LoroValue,
) -> Result<BTreeMap<String, BTreeMap<String, DocumentValue>>, ComposeError> {
    let mut documents = BTreeMap::new();
    let Some(docs) = state.get_by_key(DOCS_ROOT).cloned() else {
        return Ok(documents);
    };
    let LoroValue::Map(entries) = docs else {
        return Ok(documents);
    };
    for (document, body) in entries.unwrap() {
        let LoroValue::Map(fields) = body else {
            continue;
        };
        let mut converted = BTreeMap::new();
        for (field, value) in fields.unwrap() {
            converted.insert(field, from_loro(value)?);
        }
        documents.insert(document, converted);
    }
    Ok(documents)
}

/// One server Member: a durable engine plus a same-peer CRDT replica.
pub struct ComposedMember {
    peer_id: u64,
    engine: Engine,
    replica: ReplicaDoc,
    tenant: TenantId,
    reads: u64,
    known: BTreeSet<String>,
}

impl ComposedMember {
    /// Opens one Member's state at a durable location.
    ///
    /// # Errors
    ///
    /// Returns [`ComposeError::EngineRefused`] when the location or
    /// engine cannot open, [`ComposeError::Replica`] when the peer
    /// identity is rejected, and [`ComposeError::InvalidIdentifier`]
    /// for invalid tenant text.
    pub fn open(peer_id: u64, location: &Path, tenant_name: &str) -> Result<Self, ComposeError> {
        let engine =
            Engine::open(EngineConfig::durable(location).map_err(|_| ComposeError::EngineRefused)?)
                .map_err(|_| ComposeError::EngineRefused)?;
        let replica = ReplicaDoc::new(peer_id).map_err(ComposeError::Replica)?;
        let tenant = TenantId::try_new(tenant_name).map_err(|_| ComposeError::InvalidIdentifier)?;
        Ok(Self { peer_id, engine, replica, tenant, reads: 0, known: BTreeSet::new() })
    }

    /// Returns the Member's peer identity.
    #[must_use]
    pub const fn peer_id(&self) -> u64 {
        self.peer_id
    }

    /// Returns the Member's causal frontier.
    #[must_use]
    pub fn frontier(&self) -> ReplicaFrontier {
        self.replica.frontier()
    }

    /// Returns the merged CRDT state of the Member's scope — the
    /// convergence observation surface.
    #[must_use]
    pub fn merged_state(&self) -> LoroValue {
        self.replica.deep_value()
    }

    /// Accepts one whole-body put durably: the engine commits it and
    /// the Member's own-peer CRDT op records it.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn accept_put(
        &mut self,
        operation: &str,
        document: &str,
        body: &BTreeMap<String, DocumentValue>,
    ) -> Result<(), ComposeError> {
        self.engine_put(operation, document, body)?;
        let mut merged = BTreeMap::new();
        for (field, value) in body {
            merged.insert(field.clone(), to_loro(value));
        }
        self.replica.apply_put(document, &merged).map_err(ComposeError::Replica)?;
        self.known.insert(document.to_owned());
        Ok(())
    }

    /// Accepts one deletion durably on both planes.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn accept_delete(&mut self, operation: &str, document: &str) -> Result<(), ComposeError> {
        self.engine_delete(operation, document)?;
        self.replica.apply_delete(document).map_err(ComposeError::Replica)?;
        Ok(())
    }

    /// Returns the document body this Member's engine serves, or
    /// `None` when the engine holds no visible version.
    ///
    /// # Errors
    ///
    /// Returns [`ComposeError::EngineRefused`] when the engine refuses
    /// the read and [`ComposeError::InvalidIdentifier`] for invalid
    /// document text.
    pub fn visible(
        &mut self,
        document: &str,
    ) -> Result<Option<BTreeMap<String, DocumentValue>>, ComposeError> {
        self.reads += 1;
        let operation = OperationId::try_new(&format!("rd-{}-{}", self.peer_id, self.reads))
            .map_err(|_| ComposeError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| ComposeError::InvalidIdentifier)?;
        let scope = self.engine.scope_for(&self.tenant);
        let snapshot =
            self.engine.read_snapshot(&scope).map_err(|_| ComposeError::EngineRefused)?;
        let transaction = self
            .engine
            .begin(&scope, snapshot, operation)
            .map_err(|_| ComposeError::EngineRefused)?;
        let found = transaction.read(&document_id).map_err(|_| ComposeError::EngineRefused)?;
        drop(transaction);
        match found {
            None => Ok(None),
            Some(document) => match document.value().clone() {
                DocumentValue::Object(fields) => Ok(Some(fields)),
                // A non-object body still round-trips: one field.
                other => Ok(Some(BTreeMap::from([("value".to_owned(), other)]))),
            },
        }
    }

    /// Runs anti-entropy with one other Member and reconciles both
    /// engines to the merged state.
    ///
    /// Ownership-agnostic by construction: either Member may initiate
    /// with the other. Returns both Members' reconcile reports.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn anti_entropy_with(
        &mut self,
        other: &mut Self,
    ) -> Result<(ReconcileReport, ReconcileReport), ComposeError> {
        loop {
            let mine = self.replica.frontier();
            let theirs = other.replica.frontier();
            if mine.covers(&theirs) && theirs.covers(&mine) {
                break;
            }
            if let ReplicaExchange::Delta(bytes) =
                self.replica.delta_for(&theirs).map_err(ComposeError::Replica)?
            {
                other.replica.absorb(&bytes).map_err(ComposeError::Replica)?;
            }
            if let ReplicaExchange::Delta(bytes) =
                other.replica.delta_for(&mine).map_err(ComposeError::Replica)?
            {
                self.replica.absorb(&bytes).map_err(ComposeError::Replica)?;
            }
        }
        let first = self.reconcile()?;
        let second = other.reconcile()?;
        Ok((first, second))
    }

    /// Reconciles this Member's engine to its merged CRDT state.
    ///
    /// Idempotent per merged state: the operation identities derive
    /// from the document and the frontier fingerprint, so an unchanged
    /// frontier replays the recorded receipts and commits nothing.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn reconcile(&mut self) -> Result<ReconcileReport, ComposeError> {
        let merged = crdt_documents(&self.replica.deep_value())?;
        let fingerprint = self.frontier_fingerprint();
        let mut report = ReconcileReport::default();
        for (document, body) in &merged {
            if self.visible(document)?.as_ref() != Some(body) {
                let identity = format!("ae-{:x}-{fingerprint:x}", fnv1a(document.as_bytes()));
                self.engine_put(&identity, document, body)?;
                self.known.insert(document.clone());
                report.puts += 1;
            }
        }
        for document in self.known.clone() {
            if !merged.contains_key(&document) && self.visible(&document)?.is_some() {
                let identity = format!("aed-{:x}-{fingerprint:x}", fnv1a(document.as_bytes()));
                self.engine_delete(&identity, &document)?;
                report.deletes += 1;
            }
        }
        Ok(report)
    }

    /// Closes the Member, flushing its durable engine.
    ///
    /// # Errors
    ///
    /// Returns [`ComposeError::EngineRefused`] when the final flush
    /// fails.
    pub fn shutdown(self) -> Result<(), ComposeError> {
        self.engine.shutdown().map_err(|_| ComposeError::EngineRefused)
    }

    fn frontier_fingerprint(&self) -> u64 {
        use std::fmt::Write as _;
        let mut text = String::new();
        for (peer, end) in self.replica.frontier().ends() {
            let _ = write!(text, "{peer:x}-{end:x};");
        }
        fnv1a(text.as_bytes())
    }

    fn engine_put(
        &self,
        operation: &str,
        document: &str,
        body: &BTreeMap<String, DocumentValue>,
    ) -> Result<(), ComposeError> {
        let operation_id =
            OperationId::try_new(operation).map_err(|_| ComposeError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| ComposeError::InvalidIdentifier)?;
        let collection =
            CollectionName::try_new(COLLECTION).map_err(|_| ComposeError::InvalidIdentifier)?;
        let scope = self.engine.scope_for(&self.tenant);
        let snapshot =
            self.engine.read_snapshot(&scope).map_err(|_| ComposeError::EngineRefused)?;
        let mut transaction = self
            .engine
            .begin(&scope, snapshot, operation_id)
            .map_err(|_| ComposeError::EngineRefused)?;
        transaction
            .put(
                Document::new(document_id, collection, DocumentValue::Object(body.clone())),
                ExpectedVersion::Any,
            )
            .map_err(|_| ComposeError::EngineRefused)?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(ComposeError::EngineRefused)
            }
        }
    }

    fn engine_delete(&self, operation: &str, document: &str) -> Result<(), ComposeError> {
        let operation_id =
            OperationId::try_new(operation).map_err(|_| ComposeError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| ComposeError::InvalidIdentifier)?;
        let scope = self.engine.scope_for(&self.tenant);
        let snapshot =
            self.engine.read_snapshot(&scope).map_err(|_| ComposeError::EngineRefused)?;
        let mut transaction = self
            .engine
            .begin(&scope, snapshot, operation_id)
            .map_err(|_| ComposeError::EngineRefused)?;
        transaction
            .delete(document_id, ExpectedVersion::Any)
            .map_err(|_| ComposeError::EngineRefused)?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(()),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(ComposeError::EngineRefused)
            }
        }
    }
}
