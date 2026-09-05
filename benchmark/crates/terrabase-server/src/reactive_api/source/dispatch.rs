//! One cancellable source worker, leased only by pending origin polls.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::{Arc, Weak};

use terrabase::types::Cancellation;
use tokio::sync::Notify;

use super::{ApiError, JournalSource, Registration};

#[derive(Default)]
pub(super) struct State {
    waiters: BTreeMap<u64, Waiter>,
    running: Option<Running>,
    #[cfg(test)]
    starts: usize,
}

#[cfg(test)]
impl State {
    pub(super) fn stats(&self) -> (usize, usize, usize, bool) {
        (
            self.waiters.len(),
            self.waiters.values().map(|waiter| waiter.users).sum(),
            self.starts,
            self.running.is_some(),
        )
    }
}

struct Waiter {
    users: usize,
    changed: Arc<Notify>,
}

struct Running {
    task: tokio::task::JoinHandle<()>,
    cancellation: Cancellation,
}

struct AbortQueuedTask(tokio::task::AbortHandle);

impl Drop for AbortQueuedTask {
    fn drop(&mut self) {
        self.0.abort();
    }
}

impl Drop for Running {
    fn drop(&mut self) {
        // A blocking SQLite call cannot be forcibly interrupted. Its bounded
        // replay checks this signal before the next row/page and publishes no
        // partially accumulated buffer when cancelled.
        self.cancellation.cancel();
        self.task.abort();
    }
}

pub(in crate::reactive_api) struct Listener {
    registration: Arc<Registration>,
    changed: Arc<Notify>,
}

impl Listener {
    pub(in crate::reactive_api) fn changed(&self) -> &Arc<Notify> {
        &self.changed
    }
}

impl Drop for Listener {
    fn drop(&mut self) {
        let source = &self.registration.source;
        let Ok(mut state) = source.dispatch.lock() else { return };
        if let Some(waiter) = state.waiters.get_mut(&self.registration.id) {
            waiter.users -= 1;
            if waiter.users == 0 {
                state.waiters.remove(&self.registration.id);
            }
        }
        let running = if state.waiters.is_empty() { state.running.take() } else { None };
        drop(running);
    }
}

impl Registration {
    pub(in crate::reactive_api) fn listen(
        self: &Arc<Self>,
        after: u64,
    ) -> Result<Listener, ApiError> {
        let mut state = self.source.dispatch.lock().map_err(|_| ApiError::storage_failure())?;
        let waiter = state
            .waiters
            .entry(self.id)
            .or_insert_with(|| Waiter { users: 0, changed: Arc::new(Notify::new()) });
        waiter.users += 1;
        let changed = Arc::clone(&waiter.changed);
        if state.running.is_none() {
            let cancellation = Cancellation::new();
            let task = tokio::spawn(run(
                Arc::downgrade(&self.source),
                Arc::clone(&self.source.changed),
                cancellation.clone(),
                after,
            ));
            state.running = Some(Running { task, cancellation });
            #[cfg(test)]
            {
                state.starts += 1;
            }
        }
        // A new listener reads its view before waiting, so joining a worker
        // whose cursor has advanced cannot lose an earlier change.
        Ok(Listener { registration: Arc::clone(self), changed })
    }
}

async fn run(
    source: Weak<JournalSource>,
    changed: Arc<Notify>,
    cancellation: Cancellation,
    mut cursor: u64,
) {
    loop {
        // Register before inspecting the journal, including the first pass.
        // Broadcast hints cannot be stolen by a retiring worker while a new
        // generation starts after the last listener disconnects.
        let notified = changed.notified();
        tokio::pin!(notified);
        notified.as_mut().enable();
        tokio::time::sleep(super::super::EMISSION_CADENCE).await;
        if cancellation.is_cancelled() {
            return;
        }
        let weak = source.clone();
        let stop = cancellation.clone();
        // Storage work stays off the async executor. No strong source owner
        // survives across the worker's idle awaits.
        let task = tokio::task::spawn_blocking(move || {
            let source = weak.upgrade().ok_or_else(ApiError::storage_failure)?;
            source.dispatch_pending(cursor, &stop)
        });
        let queued = AbortQueuedTask(task.abort_handle());
        let result = task.await;
        drop(queued);
        if cancellation.is_cancelled() {
            return;
        }
        match result {
            Ok(Ok(through)) => cursor = through,
            _ => {
                let Some(source) = source.upgrade() else { return };
                // Let each pending route report its normal typed storage or
                // reset refusal; never strand waiters behind a failed pass.
                source.notify_pending(None, &cancellation);
            }
        }
        notified.await;
    }
}

impl JournalSource {
    fn dispatch_pending(&self, after: u64, cancellation: &Cancellation) -> Result<u64, ApiError> {
        if cancellation.is_cancelled() {
            return Err(ApiError::storage_failure());
        }
        let engine = self.host.engine();
        let snapshot = engine
            .read_snapshot(&engine.scope_for(&self.tenant))
            .map_err(|_| ApiError::storage_failure())?;
        let through = snapshot.sequence().get();
        if through <= after {
            return Ok(after);
        }
        let records = self.records_with_cancellation(after, through, cancellation)?;
        let affected = if let Some(records) = records {
            let mut dependencies =
                self.dependencies.lock().map_err(|_| ApiError::storage_failure())?;
            let mut affected = BTreeSet::new();
            for record in records {
                if cancellation.is_cancelled() {
                    return Err(ApiError::storage_failure());
                }
                affected.extend(dependencies.affected(&record).iter().copied());
            }
            Some(affected)
        } else {
            // A lagging worker has lost its bounded replay prefix. Pending
            // views must inspect their own exact snapshot rebuild path.
            None
        };
        if !cancellation.is_cancelled() {
            self.notify_pending(affected.as_ref(), cancellation);
        }
        Ok(through)
    }

    fn notify_pending(&self, affected: Option<&BTreeSet<u64>>, cancellation: &Cancellation) {
        let Ok(state) = self.dispatch.lock() else { return };
        if cancellation.is_cancelled() {
            return;
        }
        if let Some(affected) = affected {
            for id in affected {
                if let Some(waiter) = state.waiters.get(id) {
                    waiter.changed.notify_waiters();
                }
            }
        } else {
            for waiter in state.waiters.values() {
                waiter.changed.notify_waiters();
            }
        }
    }
}
