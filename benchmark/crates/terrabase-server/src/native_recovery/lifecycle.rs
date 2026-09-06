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
    loop {
        if let Some(until) = deadline
            && tokio::time::Instant::now() >= until
        {
            return true;
        }
        if !stopping {
            tokio::select! {
                _ = &mut stop => {
                    stopping = true;
                    deadline = Some(tokio::time::Instant::now() + DRAIN_DEADLINE);
                },
                _ = notify.notified() => {}
            }
        }
        match process().await {
            Ok(true) => {
                backoff = Duration::from_millis(25);
                continue;
            }
            Ok(false) if stopping => return false,
            Ok(false) => {
                // Notifications are coalesced; waiting here also prevents an idle spin.
                tokio::select! {
                    _ = &mut stop => {
                        stopping = true;
                        deadline = Some(tokio::time::Instant::now() + DRAIN_DEADLINE);
                    },
                    _ = notify.notified() => {}
                }
            }
            Err(error) => {
                eprintln!("native recovery worker error; retrying: {error:?}");
                if stopping {
                    let Some(until) = deadline else { return true };
                    if tokio::time::Instant::now() >= until {
                        return true;
                    }
                    tokio::time::sleep(
                        backoff.min(until.saturating_duration_since(tokio::time::Instant::now())),
                    )
                    .await;
                } else {
                    tokio::time::sleep(backoff).await;
                }
                backoff = (backoff * 2).min(MAX_BACKOFF);
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
        assert_eq!(calls.load(Ordering::SeqCst), 2);
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
        assert_eq!(calls.load(Ordering::SeqCst), 2);
    }
}
