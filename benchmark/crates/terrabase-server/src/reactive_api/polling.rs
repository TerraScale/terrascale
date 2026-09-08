//! One origin journal wait per canonical log URL, including cancellation takeover.

use super::{ApiError, Arc, LONG_POLL_WINDOW, ReadResult, ViewStore, Weak, unknown_view};

#[derive(Clone, Eq, PartialEq, Ord, PartialOrd)]
pub(super) struct LogKey {
    view: String,
    generation: u64,
    offset: u64,
}

pub(super) struct LogPoll {
    deadline: tokio::time::Instant,
    result: tokio::sync::OnceCell<Result<ReadResult, ApiError>>,
}

impl ViewStore {
    pub(super) async fn poll_log(
        &self,
        view: &str,
        generation: u64,
        offset: u64,
    ) -> Result<ReadResult, ApiError> {
        // Reject invalid cursors and serve immutable bytes without allocating
        // a waiter or joining a pending refresh at another offset.
        match self.read_retained_log(view, generation, offset)? {
            ReadResult::Empty => {}
            retained => return Ok(retained),
        }
        let key = LogKey { view: view.to_owned(), generation, offset };
        let poll = {
            let mut polls = self.log_polls.lock().map_err(|_| ApiError::storage_failure())?;
            polls.retain(|_, poll| poll.strong_count() > 0);
            if let Some(poll) = polls.get(&key).and_then(Weak::upgrade) {
                poll
            } else {
                let poll = Arc::new(LogPoll {
                    deadline: tokio::time::Instant::now() + LONG_POLL_WINDOW,
                    result: tokio::sync::OnceCell::new(),
                });
                polls.insert(key.clone(), Arc::downgrade(&poll));
                poll
            }
        };
        // OnceCell transfers initialization to another caller if the current
        // one disconnects. All callers retain the original deadline; there is
        // no detached task left behind when every request is cancelled.
        poll.result.get_or_init(|| self.wait_log(&key, poll.deadline)).await.clone()
    }

    async fn wait_log(
        &self,
        key: &LogKey,
        deadline: tokio::time::Instant,
    ) -> Result<ReadResult, ApiError> {
        #[cfg(test)]
        self.poll_starts.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let registration = {
            let views = self.views.lock().map_err(|_| ApiError::storage_failure())?;
            let view = views.get(&key.view).ok_or_else(unknown_view)?;
            view.materializer
                .as_ref()
                .and_then(|materializer| materializer.dependency.as_ref())
                .map(|registration| (Arc::clone(registration), view.observed_sequence))
        };
        let listener =
            registration.map(|(registration, after)| registration.listen(after)).transpose()?;
        let changed = listener
            .as_ref()
            .map_or_else(|| Arc::clone(&self.changed), |listener| Arc::clone(listener.changed()));
        loop {
            // Register before checking the journal so a racing commit cannot be lost.
            let notified = changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            let refresh_deadline = match self.read_log(&key.view, key.generation, key.offset)? {
                ReadResult::Empty => None,
                ReadResult::Deferred(deadline) => Some(deadline),
                ready => return Ok(ready),
            };
            if tokio::time::Instant::now() >= deadline {
                return Ok(ReadResult::Empty);
            }
            let wake = async {
                if let Some(refresh_deadline) = refresh_deadline {
                    tokio::select! {
                        () = notified => {},
                        () = tokio::time::sleep_until(refresh_deadline) => {},
                    }
                } else {
                    notified.await;
                }
            };
            if tokio::time::timeout_at(deadline, wake).await.is_err() {
                return Ok(ReadResult::Empty);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::Ordering;

    fn fixture() -> (Arc<ViewStore>, String) {
        let store = Arc::new(ViewStore::new());
        let request =
            serde_json::from_str(r#"{"databaseId":"db","statement":"SELECT * FROM users"}"#)
                .unwrap();
        let (_, view) = store.attach(&request, "tenant").unwrap();
        (store, view)
    }

    fn spawn_poll(
        store: &Arc<ViewStore>,
        view: &str,
    ) -> tokio::task::JoinHandle<Result<ReadResult, ApiError>> {
        let store = Arc::clone(store);
        let view = view.to_owned();
        tokio::spawn(async move { store.poll_log(&view, 1, 0).await })
    }

    #[tokio::test(start_paused = true)]
    async fn concurrent_origin_polls_share_one_wait_and_frozen_bytes() {
        let (store, view) = fixture();
        let tasks = (0..16).map(|_| spawn_poll(&store, &view)).collect::<Vec<_>>();
        for _ in 0..16 {
            tokio::task::yield_now().await;
        }
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 1);
        assert_eq!(store.log_polls.lock().unwrap().values().next().unwrap().strong_count(), 16);
        store.changed.notify_waiters();
        tokio::task::yield_now().await;
        assert!(tasks.iter().all(|task| !task.is_finished()));
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 1);
        assert!(store.poll_log(&view, 2, 0).await.is_err());
        assert!(store.poll_log(&view, 1, 1).await.is_err());
        store
            .append(
                &view,
                1,
                terrabase_sync_core::wire::Message::Delta {
                    subscription: view.clone(),
                    view_generation: 1,
                    from_revision: 0,
                    to_revision: 1,
                    operations: Vec::new(),
                    digest: super::super::digest_items(&[]),
                    checkpoint: super::super::checkpoint_bytes(&view, 1, 1),
                },
            )
            .unwrap();
        let ReadResult::Data { frozen: expected, .. } = store.read_log(&view, 1, 0).unwrap() else {
            panic!("published delta");
        };
        for task in tasks {
            let ReadResult::Data { revision, frozen } = task.await.unwrap().unwrap() else {
                panic!("shared data response");
            };
            assert_eq!(revision, 1);
            assert!(Arc::ptr_eq(&frozen.bytes, &expected.bytes));
        }
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 1);
        assert!(store.log_polls.lock().unwrap().values().all(|poll| poll.upgrade().is_none()));
        let ReadResult::Data { frozen, .. } = store.poll_log(&view, 1, 0).await.unwrap() else {
            panic!("retained replay");
        };
        assert!(Arc::ptr_eq(&frozen.bytes, &expected.bytes));
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 1);
    }

    #[tokio::test(start_paused = true)]
    async fn cancelled_origin_leader_transfers_wait_without_extending_deadline() {
        let (store, view) = fixture();
        let started = tokio::time::Instant::now();
        let leader = spawn_poll(&store, &view);
        tokio::task::yield_now().await;
        let follower = spawn_poll(&store, &view);
        tokio::task::yield_now().await;
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 1);
        tokio::time::advance(std::time::Duration::from_secs(20)).await;
        leader.abort();
        assert!(matches!(leader.await, Err(error) if error.is_cancelled()));
        tokio::task::yield_now().await;
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 2);
        tokio::time::advance(std::time::Duration::from_secs(4)).await;
        assert!(!follower.is_finished());
        tokio::time::advance(std::time::Duration::from_secs(1)).await;
        assert!(matches!(follower.await.unwrap().unwrap(), ReadResult::Empty));
        assert_eq!(tokio::time::Instant::now(), started + LONG_POLL_WINDOW);
        assert!(store.log_polls.lock().unwrap().values().all(|poll| poll.upgrade().is_none()));
        let fresh = spawn_poll(&store, &view);
        tokio::task::yield_now().await;
        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 3);
        assert!(!fresh.is_finished(), "timeouts are not cached for the next poll");
        fresh.abort();
        assert!(matches!(fresh.await, Err(error) if error.is_cancelled()));
        assert!(store.log_polls.lock().unwrap().values().all(|poll| poll.upgrade().is_none()));
    }

    #[tokio::test(start_paused = true)]
    async fn rejected_cursors_do_not_create_a_waiter() {
        let (store, view) = fixture();

        // These are the failures the HTTP caller must surface immediately:
        // an unknown generation, an offset beyond the current head, and an
        // unknown view. None is allowed to allocate or join a long-poll.
        assert_eq!(store.poll_log(&view, 2, 0).await.unwrap_err().code, "unknown_view");
        assert_eq!(store.poll_log(&view, 1, u64::MAX).await.unwrap_err().code, "offset_gap");
        assert_eq!(store.poll_log("missing", 1, 0).await.unwrap_err().code, "unknown_view");

        assert_eq!(store.poll_starts.load(Ordering::Relaxed), 0);
        assert!(store.log_polls.lock().unwrap().is_empty());
    }
}
