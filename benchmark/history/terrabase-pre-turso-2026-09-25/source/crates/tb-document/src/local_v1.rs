//! Strict marker-bound local-v1 open and preprovisioned-catalog verification.

use tb_core::{CollectionId, MemoryBudget};
use tb_format::{decode_active_value, decode_catalog, encode_active_key, encode_catalog_key};
use tb_replication::MAX_EVENT_CHANGES;
use tb_storage::{LocalDocumentMode, Storage};

use crate::{DocumentError, DocumentStore, DocumentStoreConfig, key, validate_document_config};

/// One expected active immutable schema binding for a local-v1 collection.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LocalSchemaBinding {
    /// Collection granted by the trusted local-v1 configuration.
    pub collection: CollectionId,
    /// Exact schema generation expected to be active before client readiness.
    pub generation: u64,
    /// Digest of the immutable schema definition expected for this generation.
    pub schema_digest: [u8; 32],
}

impl DocumentStore {
    /// Explicitly bootstrap the local-v1 mode marker for trusted setup.
    ///
    /// This method does not install schemas. The trusted fixture or operator
    /// step must call [`DocumentStore::provision_schema`] and close the store
    /// before normal startup calls [`DocumentStore::open_local_v1`]. Server
    /// startup must never call this method; it must require the existing marker.
    /// The storage gate permits an exact-marker retry after a bootstrap crash.
    ///
    /// # Errors
    /// Returns an error for invalid document limits/identity, a mode/config
    /// mismatch, an occupied raw shard, or any marker/storage bootstrap error.
    pub fn bootstrap_local_v1(
        config: DocumentStoreConfig,
        budget: MemoryBudget,
        mode: LocalDocumentMode,
    ) -> Result<Self, DocumentError> {
        validate_document_config(&config)?;
        validate_mode(&config, mode)?;
        let storage =
            Storage::bootstrap_local_document(config.storage.clone(), budget.clone(), mode)?;
        Self::open_with_storage(config, budget, storage)
    }

    /// Open a preprovisioned local-v1 shard and verify its active schemas.
    ///
    /// The storage gate requires an existing exact local-v1 marker and rejects
    /// ordinary/raw or signed-distributed storage. This method takes one
    /// snapshot for all expected catalog lookups and checks both each active
    /// pointer and its selected immutable definition before returning the
    /// store. It never creates a marker or schema.
    ///
    /// # Errors
    /// Returns an error for invalid limits/identity, a mismatched mode, a bad
    /// marker, an empty/unsorted/duplicate expected set, recovery failure, or
    /// missing/corrupt/mismatched active catalog entries.
    pub fn open_local_v1(
        config: DocumentStoreConfig,
        budget: MemoryBudget,
        mode: LocalDocumentMode,
        expected: &[LocalSchemaBinding],
    ) -> Result<Self, DocumentError> {
        validate_document_config(&config)?;
        validate_mode(&config, mode)?;
        validate_expected_bindings(expected)?;
        let storage = Storage::open_local_document(config.storage.clone(), budget.clone(), mode)?;
        let store = Self::open_with_storage(config, budget, storage)?;
        store.verify_active_bindings(expected)?;
        Ok(store)
    }

    fn verify_active_bindings(&self, expected: &[LocalSchemaBinding]) -> Result<(), DocumentError> {
        let snapshot = self.snapshot();
        for binding in expected {
            let active_key = key(encode_active_key(binding.collection))?;
            let active = self
                .storage
                .get(snapshot, &active_key)?
                .ok_or(DocumentError::LocalCatalogMismatch)?;
            let selected = decode_active_value(active.bytes())?;
            if selected.generation != binding.generation || selected.digest != binding.schema_digest
            {
                return Err(DocumentError::LocalCatalogMismatch);
            }

            let catalog_key = key(encode_catalog_key(binding.collection, selected.generation)?)?;
            let catalog = self
                .storage
                .get(snapshot, &catalog_key)?
                .ok_or(DocumentError::LocalCatalogMismatch)?;
            let definition = decode_catalog(catalog.bytes())?;
            if definition.collection != binding.collection
                || definition.generation != binding.generation
                || definition.digest != binding.schema_digest
            {
                return Err(DocumentError::LocalCatalogMismatch);
            }
        }
        Ok(())
    }
}

fn validate_mode(
    config: &DocumentStoreConfig,
    mode: LocalDocumentMode,
) -> Result<(), DocumentError> {
    let expected = LocalDocumentMode::new(
        config.storage.tenant,
        config.database,
        config.shard,
        config.origin,
        config.epoch,
        config.configuration.to_bytes(),
    );
    if mode == expected {
        Ok(())
    } else {
        Err(DocumentError::LocalModeMismatch)
    }
}

fn validate_expected_bindings(expected: &[LocalSchemaBinding]) -> Result<(), DocumentError> {
    if expected.is_empty()
        || expected.len() > MAX_EVENT_CHANGES
        || expected.iter().any(|binding| binding.generation == 0)
        || expected
            .windows(2)
            .any(|pair| pair[0].collection >= pair[1].collection)
    {
        return Err(DocumentError::LocalCatalogMismatch);
    }
    Ok(())
}
