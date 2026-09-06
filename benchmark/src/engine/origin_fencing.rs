//! Durable authority fencing for origin commits, including pre-admitted work.

use super::{Engine, EngineError};
use crate::types::StorageSequence;

impl Engine {
    /// Activates a strictly newer authority epoch, or repeats an open epoch.
    ///
    /// A closed epoch can never reopen, including after process restart.
    /// Callers must authenticate accepted authority before calling this method.
    ///
    /// # Errors
    /// Returns storage failure for stale epochs, closed-epoch reuse, or I/O failure.
    pub fn activate_origin_epoch(&self, epoch: u64) -> Result<(), EngineError> {
        if epoch == 0 { return Err(EngineError::StorageFailure); }
        let _barrier = self.origin_commit_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        self.lock_storage()?.port.set_origin_epoch(epoch, false).map_err(|()| EngineError::StorageFailure)
    }

    /// Drains origin commits and durably closes the given authority epoch.
    ///
    /// The exclusive barrier waits through publication and durability completion.
    /// Previously admitted transactions retain their original epoch and cannot
    /// publish or replay a success receipt after this fence. Replica apply is
    /// independent and may continue after this origin boundary is captured.
    /// Callers must authenticate the fencing authority before invoking this.
    ///
    /// # Errors
    /// Returns storage failure for a stale fence, poisoned lock, or I/O failure.
    pub fn fence_origin(&self, epoch: u64) -> Result<StorageSequence, EngineError> {
        let _barrier = self.origin_commit_barrier.write().map_err(|_| EngineError::StorageFailure)?;
        let mut storage = self.lock_storage()?;
        if storage.port.origin_epoch().map_err(|()| EngineError::StorageFailure)?.0 != epoch {
            return Err(EngineError::StorageFailure);
        }
        storage.port.set_origin_epoch(epoch, true).map_err(|()| EngineError::StorageFailure)?;
        Ok(storage.port.published_state().0)
    }

    /// Returns the persisted authority epoch and whether origin writes are fenced.
    ///
    /// # Errors
    /// Returns storage failure when durable state cannot be read.
    pub fn origin_epoch(&self) -> Result<(u64, bool), EngineError> {
        self.lock_storage()?.port.origin_epoch().map_err(|()| EngineError::StorageFailure)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::{CommitError, CommitOutcome, EngineConfig, Transaction};
    use crate::types::{DocumentValue, LocalDurability, OperationId, TenantId};

    fn open() -> (std::path::PathBuf, Engine) {
        let path = std::env::temp_dir().join(format!("terrabase-origin-fence-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        (path, engine)
    }

    fn write<'a>(engine: &'a Engine, id: &str) -> Transaction<'a> {
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        let mut tx = engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), OperationId::try_new(id).unwrap()).unwrap();
        tx.put_private_metadata(id, DocumentValue::Integer(1)).unwrap();
        tx
    }

    #[test]
    fn fence_rejects_preadmitted_and_replayed_origin_after_restart() {
        let (path, engine) = open();
        engine.activate_origin_epoch(1).unwrap();
        assert!(matches!(write(&engine, "committed").commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        let stale = write(&engine, "stale");
        let boundary = engine.fence_origin(1).unwrap();
        assert_eq!(stale.commit(LocalDurability::Synced), CommitOutcome::Rejected(CommitError::Conflict));
        assert_eq!(write(&engine, "committed").commit(LocalDurability::Synced), CommitOutcome::Rejected(CommitError::Conflict));
        assert_eq!(engine.published_sequence().unwrap(), boundary);
        assert!(engine.activate_origin_epoch(1).is_err());
        drop(engine);
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        assert_eq!(engine.origin_epoch().unwrap(), (1, true));
        assert_eq!(write(&engine, "committed").commit(LocalDurability::Synced), CommitOutcome::Rejected(CommitError::Conflict));
        let old = write(&engine, "old-before-promotion");
        engine.activate_origin_epoch(2).unwrap();
        assert_eq!(old.commit(LocalDurability::Synced), CommitOutcome::Rejected(CommitError::Conflict));
        assert!(engine.fence_origin(1).is_err());
        assert!(matches!(write(&engine, "new-epoch").commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }

    #[test]
    fn fence_waits_for_commit_critical_section_before_receipt() {
        let (path, engine) = open();
        engine.activate_origin_epoch(1).unwrap();
        std::thread::scope(|scope| {
            let held_commit = engine.origin_commit_barrier.read().unwrap();
            let (started_tx, started_rx) = std::sync::mpsc::channel();
            let (finished_tx, finished_rx) = std::sync::mpsc::channel();
            scope.spawn(|| {
                started_tx.send(()).unwrap();
                finished_tx.send(engine.fence_origin(1)).unwrap();
            });
            started_rx.recv().unwrap();
            assert!(finished_rx.recv_timeout(std::time::Duration::from_millis(30)).is_err());
            assert_eq!(engine.origin_epoch().unwrap(), (1, false));
            drop(held_commit);
            finished_rx.recv_timeout(std::time::Duration::from_secs(5)).unwrap().unwrap();
        });
        assert_eq!(engine.origin_epoch().unwrap(), (1, true));
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }
    #[test]
    fn crash_fence_child() {
        let Some(path) = std::env::var_os("TERRABASE_ORIGIN_FENCE_CRASH_PATH") else { return; };
        let engine = Engine::open(EngineConfig::durable(std::path::Path::new(&path)).unwrap()).unwrap();
        engine.activate_origin_epoch(7).unwrap();
        assert!(matches!(write(&engine, "before-crash").commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        engine.fence_origin(7).unwrap();
        // Skip Rust destructors, SQLite close, and Engine shutdown.
        std::process::exit(73);
    }

    #[test]
    fn acknowledged_fence_survives_process_exit_without_shutdown() {
        let (path, engine) = open();
        drop(engine);
        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "engine::origin_fencing::tests::crash_fence_child", "--nocapture"])
            .env("TERRABASE_ORIGIN_FENCE_CRASH_PATH", &path)
            .status().unwrap();
        assert_eq!(status.code(), Some(73));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        assert_eq!(engine.origin_epoch().unwrap(), (7, true));
        assert_eq!(write(&engine, "before-crash").commit(LocalDurability::Synced), CommitOutcome::Rejected(CommitError::Conflict));
        assert_eq!(write(&engine, "after-crash").commit(LocalDurability::Synced), CommitOutcome::Rejected(CommitError::Conflict));
        assert!(engine.activate_origin_epoch(7).is_err());
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }

}
