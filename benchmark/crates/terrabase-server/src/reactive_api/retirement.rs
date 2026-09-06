//! Idle materializers release hosts and admission; immutable bytes outlive them.

use std::sync::Arc;
use std::time::{Duration, SystemTime};

use super::{ApiError, Materialization, ViewStore};

const IDLE: Duration = Duration::from_secs(2 * super::CAPABILITY_BUCKET);
const SWEEP: Duration = Duration::from_mins(1);

#[cfg(test)]
mod tests;

impl ViewStore {
    pub(super) fn revoke_scope(&self, scope: &str) -> Result<(), ApiError> {
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let removed: Vec<_> =
            views.extract_if(.., |_, view| view.scope == scope).map(|(_, view)| view).collect();
        drop(views);
        // Drop hosts and admission leases outside the registry lock.
        drop(removed);
        self.changed.notify_waiters();
        Ok(())
    }

    pub(super) fn start_maintenance(self: &Arc<Self>) {
        let Ok(runtime) = tokio::runtime::Handle::try_current() else { return };
        let Ok(mut worker) = self.maintenance.lock() else { return };
        if worker.as_ref().is_some_and(|worker| !worker.is_finished()) {
            return;
        }
        let weak = Arc::downgrade(self);
        *worker = Some(runtime.spawn(async move {
            loop {
                tokio::time::sleep(SWEEP).await;
                let weak = weak.clone();
                let now = tokio::time::Instant::now();
                // Releasing a last host can close storage. Keep it off the
                // runtime worker, and hold no strong owner across idle waits.
                let alive = tokio::task::spawn_blocking(move || {
                    let Some(store) = weak.upgrade() else { return false };
                    store.retire_idle(now);
                    true
                })
                .await;
                if !matches!(alive, Ok(true)) {
                    return;
                }
            }
        }));
    }

    pub(super) fn retire_idle(&self, now: tokio::time::Instant) {
        let mut released = Vec::new();
        let Ok(mut views) = self.views.lock() else { return };
        views.retain(|_, view| {
            // A refresh or reactivation holds this lease outside the registry
            // lock. It must finish before its materializer can be retired.
            if Arc::strong_count(&view.refreshing) > 1 {
                return true;
            }
            view.prune(SystemTime::now());
            if now.saturating_duration_since(view.last_access) < IDLE {
                return true;
            }
            if let Some(retired) = view.retired_at {
                return now.saturating_duration_since(retired) <= super::RETENTION;
            }
            if let Some(guard) = view.guard.take().and_then(|guard| guard.upgrade()) {
                guard.detach_view(&view.database, &view.id);
            }
            released.extend(view.materializer.take());
            view.refresh_after = None;
            view.retired_at = Some(now);
            true
        });
        drop(views);
        drop(released);
    }

    pub(super) fn reactivate(
        &self,
        view_id: &str,
        materialization: Option<&Materialization>,
        guards: Option<&Arc<crate::guards::AbuseGuards>>,
    ) -> Result<(), ApiError> {
        let refreshing = self.view_refresh_lock(view_id)?;
        let _refreshing = refreshing.lock().map_err(|_| ApiError::storage_failure())?;
        let Some(initialized) =
            materialization.map(|value| value.initialize(&self.sources)).transpose()?
        else {
            return Ok(());
        };
        let Some(materializer) = initialized.materializer else { return Ok(()) };
        let database = {
            let views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
            let view = views.get(view_id).ok_or_else(super::unknown_view)?;
            if let Some(guards) = guards {
                guards
                    .attach_view(&view.database, view_id)
                    .map_err(|error| ApiError::abuse_guard(&error))?;
            }
            view.database.clone()
        };
        if let Err(error) =
            self.publish_items(view_id, materializer, initialized.items, initialized.revision)
        {
            if let Some(guards) = guards {
                guards.detach_view(&database, view_id);
            }
            return Err(error);
        }
        let mut views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
        let view = views.get_mut(view_id).ok_or_else(super::unknown_view)?;
        view.retired_at = None;
        view.last_access = tokio::time::Instant::now();
        view.guard = guards.map(Arc::downgrade);
        Ok(())
    }
}

impl Drop for super::View {
    fn drop(&mut self) {
        if let Some(guard) = self.guard.take().and_then(|guard| guard.upgrade()) {
            guard.detach_view(&self.database, &self.id);
        }
    }
}

impl Drop for ViewStore {
    fn drop(&mut self) {
        if let Ok(worker) = self.maintenance.get_mut()
            && let Some(worker) = worker.take()
        {
            worker.abort();
        }
    }
}

pub(super) fn retired_view() -> ApiError {
    ApiError {
        status: axum::http::StatusCode::CONFLICT,
        code: "view_retired",
        message: "the idle view no longer has an active materializer".into(),
        fix: "re-attach the query to obtain a current snapshot and capability".into(),
        placement: None,
    }
}
