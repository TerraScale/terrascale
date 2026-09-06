use std::{future::Future, sync::Arc, time::Duration};
use tokio::sync::{Notify, oneshot};

const MAX_BACKOFF: Duration = Duration::from_secs(30);
const DRAIN_DEADLINE: Duration = Duration::from_secs(5);

pub(super) async fn supervise<E, F, Fut>(
    mut stop: oneshot::Receiver<()>,
    notify: Arc<Notify>,
    process: F,
) -> bool
where
    E: std::fmt::Debug,
    F: Fn() -> Fut,
    Fut: Future<Output = Result<bool, E>>,
{
    let mut stopping = false;
    let mut deadline = None;
    let mut backoff = Duration::from_millis(25);
    let mut immediate = false;
    loop {
        if !stopping {
            match stop.try_recv() {
                Ok(()) | Err(tokio::sync::oneshot::error::TryRecvError::Closed) => {
                    stopping = true;
                    deadline = Some(tokio::time::Instant::now() + DRAIN_DEADLINE);
                }
                Err(tokio::sync::oneshot::error::TryRecvError::Empty) => {}
            }
        }
        if let Some(until) = deadline
            && tokio::time::Instant::now() >= until
        {
            return true;
        }
        if !immediate {
            tokio::select! {
                _ = &mut stop => {
                    stopping = true;
                    deadline = Some(tokio::time::Instant::now() + DRAIN_DEADLINE);
                },
                _ = notify.notified() => {}
            }
        }
        immediate = false;
        match process().await {
            Ok(true) => {
                backoff = Duration::from_millis(25);
                immediate = true;
                continue;
            }
            Ok(false) if stopping => return false,
            Ok(false) => {
                // Notifications are coalesced; waiting here also prevents an idle spin.
                immediate = false;
            }
            Err(error) => {
                eprintln!("native recovery worker error; retrying: {error:?}");
                let delay = if stopping {
                    let Some(until) = deadline else { return true };
                    if tokio::time::Instant::now() >= until {
                        return true;
                    }
                    backoff.min(until.saturating_duration_since(tokio::time::Instant::now()))
                } else {
                    backoff
                };
                if stopping {
                    tokio::time::sleep(delay).await;
                } else {
                    tokio::select! {
                        _ = tokio::time::sleep(delay) => {}
                        _ = &mut stop => {
                            stopping = true;
                            deadline = Some(tokio::time::Instant::now() + DRAIN_DEADLINE);
                        }
                    }
                }
                backoff = (backoff * 2).min(MAX_BACKOFF);
                immediate = true;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    };

    #[tokio::test]
    async fn stop_drains_until_process_reports_empty() {
        let notify = Arc::new(Notify::new());
        let (stop, stop_rx) = oneshot::channel();
        let calls = Arc::new(AtomicUsize::new(0));
        let observed = Arc::clone(&calls);
        let worker = tokio::spawn(supervise::<(), _, _>(stop_rx, notify, move || {
            let observed = Arc::clone(&observed);
            async move {
                let call = observed.fetch_add(1, Ordering::SeqCst);
                Ok(call < 2)
            }
        }));
        tokio::task::yield_now().await;
        stop.send(()).unwrap();
        worker.await.unwrap();
        assert_eq!(calls.load(Ordering::SeqCst), 3);
    }

    #[tokio::test]
    async fn transient_error_is_retried_and_then_progresses() {
        let notify = Arc::new(Notify::new());
        let (stop, stop_rx) = oneshot::channel();
        let calls = Arc::new(AtomicUsize::new(0));
        let observed = Arc::clone(&calls);
        let worker =
            tokio::spawn(supervise::<&'static str, _, _>(stop_rx, notify.clone(), move || {
                let observed = Arc::clone(&observed);
                async move {
                    let call = observed.fetch_add(1, Ordering::SeqCst);
                    if call == 0 {
                        return Err("temporary");
                    }
                    Ok(false)
                }
            }));
        notify.notify_one();
        while calls.load(Ordering::SeqCst) < 2 {
            tokio::task::yield_now().await;
        }
        stop.send(()).unwrap();
        worker.await.unwrap();
        assert_eq!(calls.load(Ordering::SeqCst), 3);
    }

    #[tokio::test(start_paused = true)]
    async fn stop_deadline_prevents_new_backlog_attempt_after_inflight_work() {
        let notify = Arc::new(Notify::new());
        let (stop, stop_rx) = oneshot::channel();
        let calls = Arc::new(AtomicUsize::new(0));
        let entered_one = Arc::new(Notify::new());
        let entered_two = Arc::new(Notify::new());
        let release_one = Arc::new(Notify::new());
        let release_two = Arc::new(Notify::new());
        let observed = Arc::clone(&calls);
        let entered_one_worker = Arc::clone(&entered_one);
        let entered_two_worker = Arc::clone(&entered_two);
        let release_one_worker = Arc::clone(&release_one);
        let release_two_worker = Arc::clone(&release_two);
        let worker = tokio::spawn(supervise::<(), _, _>(stop_rx, notify.clone(), move || {
            let observed = Arc::clone(&observed);
            let entered_one = Arc::clone(&entered_one_worker);
            let entered_two = Arc::clone(&entered_two_worker);
            let release_one = Arc::clone(&release_one_worker);
            let release_two = Arc::clone(&release_two_worker);
            async move {
                let call = observed.fetch_add(1, Ordering::SeqCst);
                if call == 0 {
                    entered_one.notify_one();
                    release_one.notified().await;
                } else if call == 1 {
                    entered_two.notify_one();
                    release_two.notified().await;
                }
                Ok(call < 2)
            }
        }));
        notify.notify_one();
        entered_one.notified().await;
        stop.send(()).unwrap();
        release_one.notify_one();
        entered_two.notified().await;
        tokio::time::advance(Duration::from_secs(6)).await;
        release_two.notify_one();
        assert!(worker.await.unwrap());
        assert_eq!(calls.load(Ordering::SeqCst), 2);
    }
}
