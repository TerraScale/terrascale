use std::time::Duration;

use tokio::time::Instant;

const MAX_ATTEMPTS: usize = 3;
const DELAY_CAP: Duration = Duration::from_secs(30);

pub(super) async fn wait(
    status: u16,
    retry_after: Option<Duration>,
    attempt: usize,
    deadline: Option<Instant>,
) -> bool {
    if !matches!(status, 429 | 503) || attempt >= MAX_ATTEMPTS {
        return false;
    }
    let Some(delay) = retry_after.map(|delay| delay.min(DELAY_CAP)) else {
        return false;
    };
    if deadline.is_some_and(|deadline| delay >= deadline.saturating_duration_since(Instant::now()))
    {
        return false;
    }
    tokio::time::sleep(delay).await;
    true
}
