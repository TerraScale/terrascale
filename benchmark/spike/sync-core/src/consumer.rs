//! The consumer prototype: a client Sync Session's receiving half.
//!
//! The consumer never writes into the sync document; it imports only
//! server-produced bytes and records the checkpoint after each absorbed
//! batch. It stands in for the native-language SDK sync clients of
//! `terrascale.sync.v2` (PD-2026-08-26-12): they implement this receiving
//! behavior natively and never multi-master the document.

use loro::{LoroDoc, LoroValue};

use crate::CONSUMER_PEER;
use crate::mapping::Checkpoint;
use crate::mapping::MappingError;

/// The error type of the consumer prototype.
#[derive(Debug)]
pub enum ConsumerError {
    /// A delta or snapshot failed to import.
    Import(String),
    /// A delta did not fully apply: pending dependencies remain, so the
    /// batch was not self-contained for this consumer's version.
    PendingDependencies,
}

impl std::fmt::Display for ConsumerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ConsumerError::Import(message) => write!(f, "import failed: {message}"),
            ConsumerError::PendingDependencies => {
                write!(f, "delta left pending dependencies unapplied")
            }
        }
    }
}

impl std::error::Error for ConsumerError {}

/// A resumable consumer session over one sync scope.
pub struct ConsumerSession {
    doc: LoroDoc,
    checkpoint: Checkpoint,
}

impl ConsumerSession {
    /// Creates a fresh session that has absorbed nothing.
    ///
    /// # Errors
    ///
    /// Returns [`MappingError::Loro`] if the consumer peer id cannot be
    /// set.
    pub fn new() -> Result<Self, MappingError> {
        let doc = LoroDoc::new();
        doc.set_peer_id(CONSUMER_PEER).map_err(|e| MappingError::Loro(e.to_string()))?;
        Ok(Self { doc, checkpoint: Checkpoint::initial() })
    }

    /// The session's current checkpoint (the durable resumption identity).
    #[must_use]
    pub fn checkpoint(&self) -> &Checkpoint {
        &self.checkpoint
    }

    /// Applies one delta batch and advances the checkpoint to the
    /// server-declared `next` checkpoint of the batch. Re-applying the
    /// same batch is idempotent (Loro de-duplicates already-applied ops),
    /// which is what makes at-least-once delivery safe.
    ///
    /// # Errors
    ///
    /// Returns [`ConsumerError::Import`] when the bytes are not a valid
    /// delta for any version, and [`ConsumerError::PendingDependencies`]
    /// when the import succeeded but left pending dependencies (a
    /// non-self-contained batch).
    pub fn apply_delta(&mut self, bytes: &[u8], next: &Checkpoint) -> Result<(), ConsumerError> {
        let status = self.doc.import(bytes).map_err(|e| ConsumerError::Import(e.to_string()))?;
        if status.pending.is_some() {
            return Err(ConsumerError::PendingDependencies);
        }
        self.checkpoint = next.clone();
        Ok(())
    }

    /// Imports a full snapshot (initial sync or forced resync) and resets
    /// the checkpoint to the version the snapshot itself carries.
    ///
    /// # Errors
    ///
    /// Returns [`ConsumerError::Import`] when the bytes are not a valid
    /// snapshot.
    pub fn import_snapshot(&mut self, bytes: &[u8]) -> Result<(), ConsumerError> {
        self.doc.import(bytes).map_err(|e| ConsumerError::Import(e.to_string()))?;
        self.refresh_checkpoint();
        Ok(())
    }

    /// The session's deep value (the convergence observation surface).
    #[must_use]
    pub fn deep_value(&self) -> LoroValue {
        self.doc.get_deep_value()
    }

    /// The session's own full-snapshot bytes (used to compare a drained
    /// consumer's history against the server's byte for byte).
    ///
    /// # Errors
    ///
    /// Returns [`ConsumerError::Import`] mapped from the export error.
    pub fn doc_snapshot(&self) -> Result<Vec<u8>, ConsumerError> {
        self.doc
            .export(loro::ExportMode::Snapshot)
            .map_err(|e| ConsumerError::Import(e.to_string()))
    }

    fn refresh_checkpoint(&mut self) {
        self.checkpoint = Checkpoint {
            vv_ends: self.doc.oplog_vv().iter().map(|(p, c)| (*p, *c)).collect(),
            storage_sequence: self.checkpoint.storage_sequence,
        };
    }
}

impl Default for ConsumerSession {
    fn default() -> Self {
        Self::new().expect("a fresh consumer doc always accepts its peer id")
    }
}
