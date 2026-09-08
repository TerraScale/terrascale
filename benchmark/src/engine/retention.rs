//! Bounded maintenance used by the member's fair database polling loop.
use super::{Engine, EngineError};

impl Engine {
    /// Expires at most `batch` keys and prunes at most `batch` complete
    /// publications that no retention window or consumer still needs.
    /// A publication is kept whole even when it contains several documents.
    /// This does not request a blocking WAL truncation checkpoint.
    ///
    /// # Errors
    /// Returns a storage failure for an invalid batch (outside 1..=1024)
    /// or failed maintenance. A later tick can safely resume committed work.
    pub fn maintain_retention(&self, batch: u32) -> Result<(), EngineError> {
        if !(1..=1024).contains(&batch) {
            return Err(EngineError::StorageFailure);
        }
        self.sweep_expired_ttl(batch)?;
        self.lock_storage()?
            .port
            .maintain_retention(batch)
            .map_err(|()| EngineError::StorageFailure)
    }
}
