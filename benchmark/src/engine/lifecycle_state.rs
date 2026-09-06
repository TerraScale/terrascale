//! Durable, bounded database lifecycle authority state.
use super::Engine;
use crate::types::TenantScope;
use std::sync::atomic::{AtomicU64, Ordering};

/// One record per admitted tenant; absent activity is explicitly unknown.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct LifecycleState {
    /// Monotonic durable state revision.
    pub generation: u64,
    /// Last flushed observed authorized request, Unix milliseconds.
    pub last_authorized_request_at: Option<u64>,
    /// Owner-controlled deletion fence.
    pub deletion_protection: bool,
    /// Accepted deletion; presence revokes public access.
    pub deletion: Option<DeletionIntent>,
}

/// Exact accepted operation identity and immutable cleanup boundaries.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DeletionIntent {
    /// Authority-issued exact replay identity.
    pub operation_id: String,
    /// Immutable accepted billing cutoff, Unix milliseconds.
    pub usage_stopped_at: u64,
    /// Authority-supplied retention boundary, Unix milliseconds.
    pub retain_until: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
/// A lifecycle command could not be applied.
pub enum LifecycleError {
    /// Durable storage could not be accessed.
    Unavailable,
    /// Scope, input or stored state was invalid.
    Invalid,
    /// Owner protection prevents deletion.
    Protected,
    /// Replay or billing cutoff conflicts with durable state.
    Conflict,
    /// Deletion has already revoked access.
    Revoked,
}

pub(crate) enum LifecycleCommand {
    Read,
    Protection(bool),
    Activity(u64),
    Delete(DeletionIntent),
}

impl Engine {
    /// Loads authoritative state.
    ///
    /// # Errors
    /// Returns an error on a scope mismatch or storage failure.
    pub fn lifecycle_state(&self, scope: &TenantScope) -> Result<LifecycleState, LifecycleError> {
        self.lifecycle_command(scope, LifecycleCommand::Read)
    }
    /// Caller must authenticate the database owner before changing protection.
    ///
    /// # Errors
    /// Errors if deletion has already fenced access or storage is unavailable.
    pub fn lifecycle_set_protection(
        &self,
        scope: &TenantScope,
        enabled: bool,
    ) -> Result<LifecycleState, LifecycleError> {
        self.lifecycle_command(scope, LifecycleCommand::Protection(enabled))
    }
    /// Flushes the aggregate observed authorized activity, monotonically.
    ///
    /// # Errors
    /// Errors if the scope or timestamp is invalid or storage is unavailable.
    pub fn lifecycle_flush_activity(
        &self,
        scope: &TenantScope,
        millis: u64,
    ) -> Result<LifecycleState, LifecycleError> {
        self.lifecycle_command(scope, LifecycleCommand::Activity(millis))
    }
    /// Caller must authenticate lifecycle authority. Atomically stops billing and
    /// records intent; an exact retry returns the original durable generation.
    ///
    /// # Errors
    /// Errors on protection, conflicting replay, invalid input or storage failure.
    pub fn lifecycle_accept_deletion(
        &self,
        scope: &TenantScope,
        intent: DeletionIntent,
    ) -> Result<LifecycleState, LifecycleError> {
        self.lifecycle_command(scope, LifecycleCommand::Delete(intent))
    }
    fn lifecycle_command(
        &self,
        scope: &TenantScope,
        command: LifecycleCommand,
    ) -> Result<LifecycleState, LifecycleError> {
        self.validate_scope(scope).map_err(|_| LifecycleError::Invalid)?;
        self.lock_storage()
            .map_err(|_| LifecycleError::Unavailable)?
            .port
            .lifecycle_update(scope.tenant(), command)
    }
}

/// One atomic aggregate per admitted database, independent of reader count.
/// Runtime periodically flushes this high-water mark; observations never do IO.
/// A crash can lose observations since the last flush, so consumers must allow
/// the configured flush lag when evaluating inactivity.
#[derive(Default)]
pub struct AuthorizedActivity(AtomicU64);
impl AuthorizedActivity {
    /// Records one already authorized request without synchronous IO.
    pub fn observe(&self, millis: u64) {
        self.0.fetch_max(millis, Ordering::Relaxed);
    }
    /// Returns the aggregate high-water mark for periodic durable flushing.
    pub fn pending(&self) -> Option<u64> {
        let value = self.0.load(Ordering::Relaxed);
        (value != 0).then_some(value)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{engine::EngineConfig, types::TenantId};

    #[test]
    fn lifecycle_restart_protection_replay_and_activity() {
        let path = std::env::temp_dir().join(format!(
            "terrabase-lifecycle-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let tenant = TenantId::try_new("tenant").unwrap();
        let scope = engine.scope_for(&tenant);
        assert_eq!(engine.lifecycle_state(&scope).unwrap(), LifecycleState::default());
        let observed = engine.lifecycle_flush_activity(&scope, 123).unwrap();
        assert_eq!(engine.lifecycle_flush_activity(&scope, 122).unwrap(), observed);
        engine.lifecycle_set_protection(&scope, true).unwrap();
        let at =
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis()
                as u64
                + 1000;
        let intent = DeletionIntent {
            operation_id: "delete-1".into(),
            usage_stopped_at: at,
            retain_until: at + 1000,
        };
        assert_eq!(
            engine.lifecycle_accept_deletion(&scope, intent.clone()),
            Err(LifecycleError::Protected)
        );
        assert_eq!(engine.storage_usage_stopped_at(&scope).unwrap(), None);
        engine.lifecycle_set_protection(&scope, false).unwrap();
        let accepted = engine.lifecycle_accept_deletion(&scope, intent.clone()).unwrap();
        assert_eq!(engine.storage_usage_stopped_at(&scope).unwrap(), Some(at));
        assert_eq!(engine.lifecycle_set_protection(&scope, true), Err(LifecycleError::Revoked));
        assert_eq!(engine.lifecycle_flush_activity(&scope, at + 1), Err(LifecycleError::Revoked));
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&tenant);
        assert_eq!(engine.lifecycle_accept_deletion(&scope, intent.clone()).unwrap(), accepted);
        let mut changed = intent;
        changed.usage_stopped_at += 1;
        assert_eq!(
            engine.lifecycle_accept_deletion(&scope, changed),
            Err(LifecycleError::Conflict)
        );
        assert_eq!(engine.lifecycle_state(&scope).unwrap(), accepted);
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }
}
