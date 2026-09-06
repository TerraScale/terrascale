//! Private webhook state, isolated from customer documents and metering.

use super::{Engine, EngineError};
use crate::types::TenantScope;

impl Engine {
    /// Loads one private webhook record for an authorized engine scope.
    ///
    /// # Errors
    /// Returns a scope, engine closure, or durable storage failure.
    pub fn webhook_metadata_load(
        &self,
        scope: &TenantScope,
        namespace: &str,
        key: &str,
    ) -> Result<Option<Vec<u8>>, EngineError> {
        self.validate_scope(scope)?;
        self.lock_storage()?
            .port
            .webhook_metadata_load(scope.tenant(), namespace, key)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Lists all private records in a namespace, in key order.
    ///
    /// Fails without returning partial results beyond 100,000 records or
    /// 64 MiB of encoded keys and records.
    ///
    /// # Errors
    /// Returns a scope, engine closure, storage, or bounded-read failure.
    pub fn webhook_metadata_list(
        &self,
        scope: &TenantScope,
        namespace: &str,
    ) -> Result<Vec<(String, Vec<u8>)>, EngineError> {
        self.validate_scope(scope)?;
        self.lock_storage()?
            .port
            .webhook_metadata_list(scope.tenant(), namespace)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Replaces one private record if its complete bytes equal `expected`.
    ///
    /// `None` requires absence. Returns `false` on conflict. Records must fit
    /// in 1 MiB. This metadata creates no document, journal row, or usage fact.
    ///
    /// # Errors
    /// Returns a scope, engine closure, storage, or oversized-record failure.
    pub fn webhook_metadata_compare_exchange(
        &self,
        scope: &TenantScope,
        namespace: &str,
        key: &str,
        expected: Option<&[u8]>,
        replacement: &[u8],
    ) -> Result<bool, EngineError> {
        self.validate_scope(scope)?;
        self.lock_storage()?
            .port
            .webhook_metadata_compare_exchange(
                scope.tenant(),
                namespace,
                key,
                expected,
                replacement,
            )
            .map_err(|()| EngineError::StorageFailure)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::EngineConfig;
    use crate::types::TenantId;

    #[test]
    fn webhook_metadata_restart_concurrent_cas_and_isolation() {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-webhook-metadata-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("clock")
                .as_nanos()
        ));
        let open =
            || Engine::open(EngineConfig::durable(&directory).expect("config")).expect("open");
        let engine = open();
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        assert!(
            engine
                .webhook_metadata_compare_exchange(&scope, "dispatch", "cursor", None, b"0")
                .unwrap()
        );
        let successes = std::thread::scope(|threads| {
            let handles: Vec<_> = (0..8)
                .map(|index| {
                    let engine = &engine;
                    let scope = &scope;
                    threads.spawn(move || {
                        engine
                            .webhook_metadata_compare_exchange(
                                scope,
                                "dispatch",
                                "cursor",
                                Some(b"0"),
                                format!("next-{index}").as_bytes(),
                            )
                            .unwrap()
                    })
                })
                .collect();
            handles.into_iter().map(|handle| usize::from(handle.join().unwrap())).sum::<usize>()
        });
        assert_eq!(successes, 1, "only one exact-byte CAS wins");
        let current = engine.webhook_metadata_load(&scope, "dispatch", "cursor").unwrap().unwrap();
        assert_ne!(current, b"0");
        assert!(engine.webhook_metadata_load(&scope, "endpoints", "cursor").unwrap().is_none());
        let other_scope = engine.scope_for(&TenantId::try_new("other").unwrap());
        assert!(engine.webhook_metadata_list(&other_scope, "dispatch").unwrap().is_empty());
        assert!(
            engine
                .webhook_metadata_compare_exchange(&scope, "secrets", "secret", None, b"sensitive")
                .unwrap()
        );
        assert!(
            engine
                .webhook_metadata_compare_exchange(
                    &scope,
                    "secrets",
                    "large",
                    None,
                    &vec![0; 1024 * 1024 + 1]
                )
                .is_err()
        );
        let snapshot = engine.read_snapshot(&scope).unwrap();
        assert!(engine.snapshot_documents(&scope, &snapshot).unwrap().is_empty());
        assert!(engine.journal_rows(&scope, 0, 100).unwrap().is_empty());
        assert_eq!(engine.metering_live_bytes(&scope), 0);
        assert!(engine.metering_fact(&scope, "tenant").is_none());
        engine.shutdown().unwrap();
        let engine = open();
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        assert_eq!(
            engine.webhook_metadata_list(&scope, "dispatch").unwrap(),
            vec![("cursor".into(), current)]
        );
        assert_eq!(
            engine.webhook_metadata_load(&scope, "secrets", "secret").unwrap(),
            Some(b"sensitive".to_vec())
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(directory).unwrap();
    }
}
