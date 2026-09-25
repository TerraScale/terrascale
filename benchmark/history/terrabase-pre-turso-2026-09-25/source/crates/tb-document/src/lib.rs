//! Exclusive, one-shard document catalog and local document access.
//!
//! A `DocumentStore` owns its raw storage handle. It never exposes that handle,
//! so callers cannot bypass the reserved C0..EF document namespace.

#![forbid(unsafe_code)]

use tb_core::{
    AdmissionError, Attribution, CollectionId, CommitReceipt, DatabaseId, DocumentId, Key,
    MemoryBudget, MemoryCategory, OriginId, Reservation, ShardId, TransactionId,
};
use tb_format::{
    ActiveSchema, DocumentEventId, DocumentKind, DocumentSibling, FormatError, decode_active_value,
    decode_catalog, decode_document, decode_state_with_limits, encode_active_key,
    encode_active_value, encode_catalog_into, encode_catalog_key, encode_primary_key,
    validate_document_schema,
};
use tb_replication::{CausalContext, ConfigurationId, ReplicationLimits};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

mod distributed;
mod distributed_apply;
mod distributed_producer;
mod distributed_read;
mod distributed_state;
mod local_v1;
mod producer;
pub use distributed::{DistributedDocumentStore, open_distributed};
pub use distributed_apply::DocumentApplyOutcome;
pub use distributed_producer::{DistributedDocumentCommit, DistributedSourceFrame};
pub use local_v1::LocalSchemaBinding;
pub use producer::{DocumentCommit, DocumentWrite, DocumentWriteValue};

/// A locally owned document shard's persisted event identity and resource caps.
#[derive(Clone)]
pub struct DocumentStoreConfig {
    pub storage: StorageConfig,
    pub database: DatabaseId,
    pub shard: ShardId,
    pub origin: OriginId,
    pub epoch: tb_core::MembershipEpoch,
    pub configuration: ConfigurationId,
    pub replication: ReplicationLimits,
}

/// One fully copied visible sibling. Live bytes decode as one `TBDOC001`.
#[derive(Clone, Debug)]
pub struct OwnedSibling {
    pub event: DocumentEventId,
    pub generation: u64,
    pub schema_digest: [u8; 32],
    pub kind: DocumentKind,
    pub document_bytes: Vec<u8>,
}

/// A point read keeps the complete causal token and never hides a conflict.
#[derive(Clone, Debug)]
pub enum DocumentPoint {
    Absent,
    Deleted {
        context: CausalContext,
        sibling: OwnedSibling,
    },
    Value {
        context: CausalContext,
        sibling: OwnedSibling,
    },
    Conflict {
        context: CausalContext,
        siblings: Vec<OwnedSibling>,
    },
}

/// Point result together with the admission claim for its owned sibling bytes.
pub struct DocumentRead {
    point: DocumentPoint,
    _permit: Reservation,
}

impl DocumentRead {
    #[must_use]
    pub const fn point(&self) -> &DocumentPoint {
        &self.point
    }
}

/// Failure at the exclusive local document boundary.
#[derive(Debug, thiserror::Error)]
pub enum DocumentError {
    #[error(transparent)]
    Core(#[from] tb_core::CoreError),
    #[error(transparent)]
    Format(#[from] FormatError),
    #[error("submitted document fields fail the active schema: {0}")]
    InvalidDocumentFields(#[source] FormatError),
    #[error(transparent)]
    Storage(#[from] StorageError),
    #[error(transparent)]
    Replication(#[from] tb_replication::ReplicationError),
    #[error(transparent)]
    Receive(#[from] tb_replication::ReceiveError),
    #[error(transparent)]
    Metadata(#[from] tb_replication::MetadataError),
    #[error(transparent)]
    Admission(#[from] AdmissionError),
    #[error("active collection schema changed")]
    SchemaChanged,
    #[error("local document mode identity does not match the configured shard")]
    LocalModeMismatch,
    #[error("preprovisioned local catalog does not match configured bindings")]
    LocalCatalogMismatch,
    #[error("document changes not strictly sorted")]
    InvalidWriteOrder,
    #[error("invalid observed causal context: {0}")]
    InvalidCausalContext(&'static str),
    #[error("document shard corruption: {0}")]
    Corrupt(&'static str),
    #[error("incoming certified event conflicts with durable inbox bytes")]
    IncomingConflict,
    #[error("document operation exceeds one-shard v1 limits: {0}")]
    Capacity(&'static str),
    #[error("document shard requires reopen after an uncertain write")]
    NeedsRecovery,
    #[error("document commit requires recovery after verification failed: {0}")]
    CommitUncertain(#[source] Box<DocumentError>),
}

/// Single-owner facade for one local-durable document shard.
pub struct DocumentStore {
    storage: Storage,
    database: DatabaseId,
    shard: ShardId,
    origin: OriginId,
    epoch: tb_core::MembershipEpoch,
    configuration: ConfigurationId,
    limits: ReplicationLimits,
    format: tb_format::FormatLimits,
    budget: MemoryBudget,
    write_attribution: Attribution,
    query_attribution: Attribution,
    local_sequence: u64,
    poisoned: bool,
    #[cfg(test)]
    fail_frame_lookup_once: std::sync::atomic::AtomicBool,
}

impl DocumentStore {
    /// Open a dedicated shard and reconcile its local-origin high-water.
    ///
    /// # Errors
    /// Returns an error for storage corruption, invalid identity, or an
    /// inconsistent local event tail.
    pub fn open(config: DocumentStoreConfig, budget: MemoryBudget) -> Result<Self, DocumentError> {
        validate_document_config(&config)?;
        let storage = Storage::open(config.storage.clone(), budget.clone())?;
        Self::open_with_storage(config, budget, storage)
    }

    fn open_with_storage(
        config: DocumentStoreConfig,
        budget: MemoryBudget,
        storage: Storage,
    ) -> Result<Self, DocumentError> {
        let DocumentStoreConfig {
            storage: storage_config,
            database,
            shard,
            origin,
            epoch,
            configuration,
            replication,
        } = config;
        let format = storage_config.format;
        let write_attribution = Attribution {
            tenant: storage_config.tenant,
            worker: storage_config.worker,
            category: MemoryCategory::WriteBuffer,
        };
        let query_attribution = Attribution {
            tenant: storage_config.tenant,
            worker: storage_config.worker,
            category: MemoryCategory::Query,
        };
        let document_budget = budget;
        distributed::reject_distributed_local_state(
            &storage,
            &storage_config,
            &document_budget,
            replication,
        )?;
        let mut store = Self {
            storage,
            database,
            shard,
            origin,
            epoch,
            configuration,
            limits: replication,
            format,
            budget: document_budget,
            write_attribution,
            query_attribution,
            local_sequence: 0,
            poisoned: false,
            #[cfg(test)]
            fail_frame_lookup_once: std::sync::atomic::AtomicBool::new(false),
        };
        store.reconcile_local_tail()?;
        Ok(store)
    }

    /// Current local visibility cut for subsequent typed reads.
    #[must_use]
    pub fn snapshot(&self) -> tb_core::ReadSnapshot {
        self.storage.snapshot()
    }

    /// Provision an immutable schema generation and switch its active pointer
    /// together in one durable local batch. Existing matching bytes are a no-op.
    ///
    /// # Errors
    /// Returns `SchemaChanged` if the supplied previous generation is stale,
    /// or corruption if an immutable generation has different bytes.
    pub fn provision_schema(
        &mut self,
        transaction_id: TransactionId,
        collection: CollectionId,
        generation: u64,
        previous_generation: u64,
        schema_bytes: &[u8],
    ) -> Result<Option<CommitReceipt>, DocumentError> {
        if self.poisoned {
            return Err(DocumentError::NeedsRecovery);
        }
        let _permit = self.budget.try_reserve(
            self.write_attribution,
            schema_bytes
                .len()
                .checked_mul(2)
                .and_then(|n| n.checked_add(256))
                .ok_or(DocumentError::Capacity("schema work bytes"))?,
        )?;
        let mut catalog_bytes = Vec::new();
        let digest = encode_catalog_into(
            &mut catalog_bytes,
            collection,
            generation,
            previous_generation,
            schema_bytes,
        )?;
        let active_bytes = encode_active_value(ActiveSchema { generation, digest })?;
        let catalog_key = key(encode_catalog_key(collection, generation)?)?;
        let active_key = key(encode_active_key(collection))?;
        let snapshot = self.storage.snapshot();
        let existing_catalog = self.storage.get(snapshot, &catalog_key)?;
        let active = self.storage.get(snapshot, &active_key)?;
        if let Some(existing) = &existing_catalog {
            if existing.bytes() != catalog_bytes {
                return Err(DocumentError::Corrupt(
                    "immutable catalog generation redefined",
                ));
            }
            if active
                .as_ref()
                .is_some_and(|value| value.bytes() == active_bytes)
            {
                return Ok(None);
            }
        }
        match (previous_generation, active.as_ref()) {
            (0, None) => {}
            (0, Some(_)) | (_, None) => return Err(DocumentError::SchemaChanged),
            (previous, Some(bytes)) => {
                let selected = decode_active_value(bytes.bytes())?;
                if selected.generation != previous {
                    return Err(DocumentError::SchemaChanged);
                }
                let previous_key = key(encode_catalog_key(collection, previous)?)?;
                let previous_bytes = self
                    .storage
                    .get(snapshot, &previous_key)?
                    .ok_or(DocumentError::Corrupt("active catalog definition missing"))?;
                let definition = decode_catalog(previous_bytes.bytes())?;
                if definition.collection != collection || definition.digest != selected.digest {
                    return Err(DocumentError::Corrupt("active catalog binding mismatch"));
                }
            }
        }
        let mutations = [
            Mutation::Put {
                key: catalog_key,
                value: catalog_bytes,
            },
            Mutation::Put {
                key: active_key,
                value: active_bytes.to_vec(),
            },
        ];
        match self
            .storage
            .commit_batch(snapshot, transaction_id, &mutations)
        {
            Ok(receipt) => Ok(Some(receipt)),
            Err(error) => {
                if matches!(error, StorageError::Io(_) | StorageError::Poisoned) {
                    self.poisoned = true;
                }
                Err(error.into())
            }
        }
    }

    /// Read all visible siblings for a document at one storage snapshot.
    /// Each live sibling is checked against its retained immutable schema.
    ///
    /// # Errors
    /// Returns an error if primary state or a referenced catalog is corrupt.
    pub fn read(
        &self,
        snapshot: tb_core::ReadSnapshot,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentError> {
        let primary_key = key(encode_primary_key(collection, document))?;
        let Some(value) = self.storage.get(snapshot, &primary_key)? else {
            return Ok(DocumentRead {
                point: DocumentPoint::Absent,
                _permit: self.budget.try_reserve(self.query_attribution, 0)?,
            });
        };
        let state = decode_state_with_limits(
            value.bytes(),
            self.limits.max_origins,
            self.limits.max_siblings,
        )?;
        if state.collection != collection || state.document != document {
            return Err(DocumentError::Corrupt("primary state key/value mismatch"));
        }
        let owned_bytes = value
            .bytes()
            .len()
            .checked_add(state.siblings.len() * 128)
            .and_then(|n| n.checked_add(state.context.len() * 80))
            .ok_or(DocumentError::Capacity("point output bytes"))?;
        let permit = self
            .budget
            .try_reserve(self.query_attribution, owned_bytes)?;
        let mut siblings = Vec::new();
        siblings
            .try_reserve_exact(state.siblings.len())
            .map_err(|_| DocumentError::Capacity("point sibling allocation"))?;
        for sibling in state.siblings {
            self.check_sibling_catalog(snapshot, collection, document, sibling)?;
            siblings.push(OwnedSibling {
                event: sibling.event,
                generation: sibling.generation,
                schema_digest: sibling.schema_digest,
                kind: sibling.kind,
                document_bytes: sibling.document_bytes.to_vec(),
            });
        }
        let context = CausalContext::from_entries(
            state.context.iter().map(|dot| (dot.origin, dot.sequence)),
            self.limits.max_origins,
        )?;
        let point = match siblings.len() {
            0 => Err(DocumentError::Corrupt("primary state has no siblings")),
            1 => {
                let sibling = siblings
                    .pop()
                    .ok_or(DocumentError::Corrupt("primary state has no sibling"))?;
                if sibling.kind == DocumentKind::Tombstone {
                    Ok(DocumentPoint::Deleted { context, sibling })
                } else {
                    Ok(DocumentPoint::Value { context, sibling })
                }
            }
            _ => Ok(DocumentPoint::Conflict { context, siblings }),
        }?;
        Ok(DocumentRead {
            point,
            _permit: permit,
        })
    }

    fn check_sibling_catalog(
        &self,
        snapshot: tb_core::ReadSnapshot,
        collection: CollectionId,
        document: DocumentId,
        sibling: DocumentSibling<'_>,
    ) -> Result<(), DocumentError> {
        let catalog_key = key(encode_catalog_key(collection, sibling.generation)?)?;
        let definition_bytes = self
            .storage
            .get(snapshot, &catalog_key)?
            .ok_or(DocumentError::Corrupt("sibling catalog definition missing"))?;
        let definition = decode_catalog(definition_bytes.bytes())?;
        if definition.collection != collection
            || definition.generation != sibling.generation
            || definition.digest != sibling.schema_digest
            || sibling.event.database != self.database
            || sibling.event.shard != self.shard
        {
            return Err(DocumentError::Corrupt(
                "sibling catalog or shard binding mismatch",
            ));
        }
        if sibling.kind == DocumentKind::Live {
            let document_view = decode_document(sibling.document_bytes)?;
            if document_view.binding().document != document {
                return Err(DocumentError::Corrupt("sibling document ID mismatch"));
            }
            validate_document_schema(document_view, &definition.schema)?;
        }
        Ok(())
    }
}

fn key<const N: usize>(bytes: [u8; N]) -> Result<Key, tb_core::CoreError> {
    Key::new(bytes.to_vec())
}

fn validate_document_config(config: &DocumentStoreConfig) -> Result<(), DocumentError> {
    config.replication.validate()?;
    if config.origin.incarnation == [0; 16] || config.epoch.get() == 0 {
        return Err(DocumentError::Corrupt("invalid local event identity"));
    }
    Ok(())
}
