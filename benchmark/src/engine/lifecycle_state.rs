//! Durable, bounded database lifecycle authority state.
use super::Engine;
use crate::types::TenantScope;
use std::sync::atomic::{AtomicU64, Ordering};

/// One record per admitted tenant; absent activity is explicitly unknown.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct LifecycleState {
    /// Reversible owner-requested pause; retained data and accounting remain intact.
    pub paused: bool,
    /// Monotonic durable state revision.
    pub generation: u64,
    /// Last flushed observed authorized request, Unix milliseconds.
    pub last_authorized_request_at: Option<u64>,
    /// Owner-controlled deletion fence.
    pub deletion_protection: bool,
    /// Accepted deletion; presence revokes public access.
    pub deletion: Option<DeletionIntent>,
    /// Durable quiescence intent; presence revokes access before billing stops.
    pub deletion_preparation: Option<DeletionPreparation>,
}

/// Immutable result retained for one pause/resume operation identity.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LifecycleOperationReceipt {
    /// Operation identity.
    pub operation_id: String,
    /// Applied action (`pause` or `resume`).
    pub action: String,
    /// Resulting paused value at the operation boundary.
    pub paused: bool,
    /// Lifecycle generation assigned by the operation.
    pub generation: u64,
}

/// Current state together with the immutable receipt for the requested action.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LifecycleOperationOutcome {
    /// Current state, which may have advanced after the operation.
    pub state: LifecycleState,
    /// Immutable applied or replayed operation result.
    pub receipt: LifecycleOperationReceipt,
}

/// Durable preparation receipt after origin commits have drained.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DeletionPreparation {
    /// Authority-issued identity shared with the final acceptance.
    pub operation_id: String,
    /// Minimum common cutoff, Unix milliseconds, observed after quiescence.
    pub minimum_cutoff_millis: u64,
}

impl LifecycleState {
    /// Whether preparation or acceptance has revoked public access.
    #[must_use]
    pub fn access_revoked(&self) -> bool {
        self.deletion_preparation.is_some() || self.deletion.is_some()
    }
}

/// Exact accepted operation identity and immutable cleanup boundaries.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DeletionIntent {
    /// Authority-issued exact replay identity.
    pub operation_id: String,
    /// Immutable accepted billing cutoff, Unix milliseconds.
    pub usage_stopped_at: u64,
    /// Authority-supplied retention boundary, Unix milliseconds.
    pub retain_until: Option<u64>,
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

#[derive(Clone)]
pub(crate) enum LifecycleCommand {
    Read,
    Prepare(String),
    Protection(bool),
    Activity(u64),
    Delete(DeletionIntent),
    Pause(String),
    Resume(String),
}

impl Engine {
    /// Drains origin commits and atomically records preparation with its fence.
    /// Billing continues until acceptance. Caller authenticates lifecycle authority.
    ///
    /// # Errors
    /// Returns protection, conflicting identity, scope, clock, or storage errors.
    pub fn lifecycle_prepare_deletion(
        &self,
        scope: &TenantScope,
        operation_id: &str,
    ) -> Result<LifecycleState, LifecycleError> {
        let _barrier =
            self.origin_commit_barrier.write().map_err(|_| LifecycleError::Unavailable)?;
        self.lifecycle_command(scope, LifecycleCommand::Prepare(operation_id.to_owned()))
    }

    /// Loads authoritative state.
    ///
    /// # Errors
    /// Returns an error on a scope mismatch or storage failure.
    pub fn lifecycle_state(&self, scope: &TenantScope) -> Result<LifecycleState, LifecycleError> {
        let state = self.lifecycle_command(scope, LifecycleCommand::Read)?;
        self.cache_lifecycle_pause(scope, &state);
        Ok(state)
    }

    pub(crate) fn lifecycle_is_paused(&self, scope: &TenantScope) -> Result<bool, LifecycleError> {
        if let Ok(cache) = self.lifecycle_paused.lock()
            && let Some((_, paused)) = cache.get(scope.tenant().as_str())
        {
            return Ok(*paused);
        }
        Ok(self.lifecycle_state(scope)?.paused)
    }

    fn cache_lifecycle_pause(&self, scope: &TenantScope, state: &LifecycleState) {
        if let Ok(mut cache) = self.lifecycle_paused.lock() {
            let entry = cache.entry(scope.tenant().as_str().to_owned()).or_insert((0, false));
            // A concurrent authoritative read may finish after a newer pause
            // or resume; never let that older observation replace the fence.
            if state.generation >= entry.0 {
                *entry = (state.generation, state.paused);
            }
        }
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

    /// Pauses authorized activity for a tenant while retaining its data and accounting state.
    /// The origin commit barrier drains before the durable pause is recorded.
    ///
    /// # Errors
    /// Returns an invalid-scope or unavailable-storage error.
    pub fn lifecycle_pause(
        &self,
        scope: &TenantScope,
        operation_id: &str,
    ) -> Result<LifecycleOperationOutcome, LifecycleError> {
        let _barrier =
            self.origin_commit_barrier.write().map_err(|_| LifecycleError::Unavailable)?;
        self.validate_scope(scope).map_err(|_| LifecycleError::Invalid)?;
        let state = self
            .lock_storage()
            .map_err(|_| LifecycleError::Unavailable)?
            .port
            .lifecycle_operation(
                scope.tenant(),
                LifecycleCommand::Pause(operation_id.to_owned()),
            )?;
        self.cache_lifecycle_pause(scope, &state.state);
        Ok(state)
    }

    /// Resumes authorized activity for a tenant by clearing its durable pause state.
    ///
    /// # Errors
    /// Returns an invalid-scope or unavailable-storage error.
    pub fn lifecycle_resume(
        &self,
        scope: &TenantScope,
        operation_id: &str,
    ) -> Result<LifecycleOperationOutcome, LifecycleError> {
        self.validate_scope(scope).map_err(|_| LifecycleError::Invalid)?;
        let state = self
            .lock_storage()
            .map_err(|_| LifecycleError::Unavailable)?
            .port
            .lifecycle_operation(
                scope.tenant(),
                LifecycleCommand::Resume(operation_id.to_owned()),
            )?;
        self.cache_lifecycle_pause(scope, &state.state);
        Ok(state)
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
        let paused = engine.lifecycle_pause(&scope, "pause-1").unwrap();
        assert!(paused.state.paused);
        assert!(paused.state.deletion_protection);
        assert_eq!(paused.state.last_authorized_request_at, Some(123));
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&tenant);
        assert!(engine.lifecycle_state(&scope).unwrap().paused);
        let resumed = engine.lifecycle_resume(&scope, "resume-1").unwrap();
        assert!(!resumed.state.paused);
        assert!(resumed.state.deletion_protection);
        let at = u64::try_from(
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis(),
        )
        .unwrap()
            + 1000;
        let intent = DeletionIntent {
            operation_id: "delete-1".into(),
            usage_stopped_at: at,
            retain_until: Some(at + 1000),
        };
        assert_eq!(
            engine.lifecycle_accept_deletion(&scope, intent.clone()),
            Err(LifecycleError::Protected)
        );
        assert_eq!(engine.storage_usage_stopped_at(&scope).unwrap(), None);
        engine.lifecycle_set_protection(&scope, false).unwrap();
        let prepared = engine.lifecycle_prepare_deletion(&scope, "delete-1").unwrap();
        assert!(prepared.access_revoked());
        assert!(prepared.deletion.is_none());
        assert_eq!(engine.storage_usage_stopped_at(&scope).unwrap(), None);
        assert!(engine.origin_epoch().unwrap().1);
        assert!(engine.activate_origin_epoch(2).is_err());
        assert_eq!(engine.lifecycle_set_protection(&scope, true), Err(LifecycleError::Revoked));
        assert_eq!(
            engine.lifecycle_prepare_deletion(&scope, "different"),
            Err(LifecycleError::Conflict)
        );
        assert_eq!(engine.lifecycle_prepare_deletion(&scope, "delete-1").unwrap(), prepared);
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&tenant);
        assert_eq!(engine.lifecycle_prepare_deletion(&scope, "delete-1").unwrap(), prepared);
        assert!(engine.activate_origin_epoch(2).is_err());
        let at = at.max(prepared.deletion_preparation.as_ref().unwrap().minimum_cutoff_millis);
        let intent =
            DeletionIntent { usage_stopped_at: at, retain_until: Some(at + 1000), ..intent };
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

    #[test]
    fn lifecycle_noop_operations_keep_receipts_across_restart_and_replay() {
        let path = std::env::temp_dir().join(format!(
            "terrabase-lifecycle-noop-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let tenant = TenantId::try_new("tenant").unwrap();
        let scope = engine.scope_for(&tenant);

        let resumed = engine.lifecycle_resume(&scope, "resume-initial").unwrap();
        assert!(!resumed.state.paused);
        assert_eq!(resumed.state.generation, 0);
        assert_eq!(resumed.receipt.generation, 0);
        assert_eq!(resumed.receipt.action, "resume");

        let paused = engine.lifecycle_pause(&scope, "pause-first").unwrap();
        assert_eq!(paused.state.generation, 1);
        let noop_pause = engine.lifecycle_pause(&scope, "pause-second").unwrap();
        assert!(noop_pause.state.paused);
        assert_eq!(noop_pause.state.generation, 1);
        assert_eq!(noop_pause.receipt.generation, 1);
        assert_ne!(paused.receipt.operation_id, noop_pause.receipt.operation_id);

        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&tenant);
        let resumed = engine.lifecycle_resume(&scope, "resume-after-restart").unwrap();
        assert!(!resumed.state.paused);
        assert_eq!(resumed.state.generation, 2);

        let replay = engine.lifecycle_pause(&scope, "pause-second").unwrap();
        assert!(!replay.state.paused);
        assert_eq!(replay.receipt, noop_pause.receipt);
        assert_eq!(engine.lifecycle_resume(&scope, "pause-second"), Err(LifecycleError::Conflict));

        engine.lifecycle_prepare_deletion(&scope, "delete-1").unwrap();
        assert_eq!(
            engine.lifecycle_pause(&scope, "pause-after-revoke"),
            Err(LifecycleError::Revoked)
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }
}
